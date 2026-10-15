using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using CallReceiver.Models;

namespace CallReceiver.Services;

internal sealed record StoredLog(int Version, string Kind, DateTimeOffset At,
    LogEntry? Entry = null, string? EventId = null, string? PopupState = null);

// All file mutations, including clear and shutdown flush, run on one reader.
internal sealed class LogFileStore : IAsyncDisposable
{
    private sealed record Work(StoredLog? Record, bool Clear, TaskCompletionSource? Done);
    private readonly Channel<Work> queue = Channel.CreateUnbounded<Work>(new UnboundedChannelOptions
        { SingleReader = true, AllowSynchronousContinuations = false });
    private readonly Action<string> warning;
    private readonly Func<DateTimeOffset> now;
    private readonly Task worker;
    private StreamWriter? writer;
    private string? openPath;
    private static readonly JsonSerializerOptions Json = new(JsonDefaults.Options) { WriteIndented = false };
    public string DirectoryPath { get; }
    public Task<IReadOnlyList<LogEntry>> Restored { get; }

    public LogFileStore(string directory, Action<string> warning, Func<DateTimeOffset> now)
    {
        DirectoryPath = Path.Combine(Path.GetFullPath(directory), "logs");
        this.warning = warning; this.now = now;
        Restored = Task.Run(ReadRecent);
        worker = Task.Run(WriteLoopAsync);
    }
    public void Append(LogEntry entry) => queue.Writer.TryWrite(new(new(1, "entry", now(), entry), false, null));
    public void Popup(string eventId, string state) => queue.Writer.TryWrite(new(new(1, "popup", now(), EventId: eventId, PopupState: state), false, null));
    public Task ClearAsync() => Barrier(true);
    public Task FlushAsync() => Barrier(false);
    private Task Barrier(bool clear)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!queue.Writer.TryWrite(new(null, clear, done))) done.SetException(new IOException("로그 작성기가 종료되었습니다."));
        return done.Task;
    }
    private static bool Owned(string path)
    {
        var name = Path.GetFileName(path);
        return name.Length == 25 && name.StartsWith("CallReceiver-", StringComparison.Ordinal) &&
            name.EndsWith(".log", StringComparison.Ordinal) && DateOnly.TryParseExact(name.Substring(13, 8),
                "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
    }
    private IEnumerable<string> Files() => Directory.Exists(DirectoryPath)
        ? Directory.EnumerateFiles(DirectoryPath, "CallReceiver-*.log", SearchOption.TopDirectoryOnly)
            .Where(Owned).OrderByDescending(Path.GetFileName, StringComparer.Ordinal)
        : [];
    private static IEnumerable<string> ReverseLines(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var block = new byte[65536];
        var line = new List<byte>();
        var oversized = false;
        var position = file.Length;
        while (position > 0)
        {
            var count = (int)Math.Min(position, block.Length);
            position -= count;
            file.Position = position;
            file.ReadExactly(block.AsSpan(0, count));
            for (var i = count - 1; i >= 0; i--)
            {
                if (block[i] == (byte)'\n')
                {
                    if (oversized) yield return "invalid oversized log record";
                    else if (line.Count > 0) { line.Reverse(); yield return Encoding.UTF8.GetString(line.ToArray()).TrimEnd('\r'); }
                    line.Clear(); oversized = false;
                }
                else if (!oversized)
                {
                    if (line.Count < 1024 * 1024) line.Add(block[i]);
                    else { line.Clear(); oversized = true; }
                }
            }
        }
        if (oversized) yield return "invalid oversized log record";
        else if (line.Count > 0) { line.Reverse(); yield return Encoding.UTF8.GetString(line.ToArray()).TrimEnd('\r'); }
    }
    private IEnumerable<StoredLog> ReadRecords()
    {
        string[] files;
        try { files = Files().ToArray(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { warning("로그 파일 목록을 읽을 수 없습니다: " + e.Message); yield break; }
        foreach (var file in files)
        {
            using var lines = ReverseLines(file).GetEnumerator();
            while (true)
            {
                string line;
                try { if (!lines.MoveNext()) break; line = lines.Current; }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                { warning("로그 파일을 읽을 수 없습니다: " + e.Message); break; }
                StoredLog? record;
                try
                {
                    record = JsonSerializer.Deserialize<StoredLog>(line, Json);
                    if (record is null || record.Version != 1 ||
                        (record.Kind != "entry" && record.Kind != "popup") ||
                        (record.Kind == "entry" && (record.Entry is null || string.IsNullOrEmpty(record.Entry.Id) || record.Entry.Message is null)) ||
                        (record.Kind == "popup" && (record.EventId is null || record.PopupState is null)))
                        throw new JsonException("지원하지 않거나 불완전한 로그 기록");
                }
                catch (JsonException) { warning("일부 손상되거나 불완전한 로그 기록을 건너뛰었습니다."); continue; }
                yield return record;
            }
        }
    }
    private IReadOnlyList<LogEntry> ReadRecent()
    {
        var entries = new List<LogEntry>();
        var ids = new HashSet<string>();
        foreach (var record in ReadRecords())
        {
            if (record.Entry is { } entry && ids.Add(entry.Id)) entries.Add(entry);
            if (entries.Count == 200) break;
        }
        if (entries.Count == 0) return entries;
        // A second bounded-memory pass only tracks popup updates for the selected entries.
        var eventIds = entries.Select(e => e.Detail?.EventId).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var states = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var oldestId = entries[^1].Id;
        foreach (var record in ReadRecords())
        {
            if (record.Kind == "popup" && record.EventId is { } id && eventIds.Contains(id))
                states.TryAdd(id, record.PopupState!);
            if (record.Entry?.Id == oldestId) break;
        }
        return entries.Select(e => e.Detail?.EventId is { } id && states.TryGetValue(id, out var state)
            ? e with { Detail = e.Detail with { PopupState = state } } : e).ToArray();
    }
    private async Task CloseWriterAsync()
    {
        var previous = writer;
        writer = null; openPath = null;
        if (previous is not null) await previous.DisposeAsync();
    }
    private async Task WriteAsync(StoredLog record)
    {
        Directory.CreateDirectory(DirectoryPath);
        var korean = TimeZoneInfo.ConvertTime(record.At, TimeZoneInfo.FindSystemTimeZoneById("Korea Standard Time"));
        var path = Path.Combine(DirectoryPath, "CallReceiver-" + korean.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".log");
        if (path != openPath)
        {
            await CloseWriterAsync();
            var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read,
                4096, FileOptions.Asynchronous);
            // Separate an interrupted last record from the next valid JSON line.
            if (stream.Length > 0)
            {
                stream.Position = stream.Length - 1;
                if (stream.ReadByte() != (byte)'\n') { stream.Position = stream.Length; stream.WriteByte((byte)'\n'); }
            }
            stream.Position = stream.Length;
            writer = new StreamWriter(stream, new UTF8Encoding(false));
            openPath = path;
        }
        await writer!.WriteLineAsync(JsonSerializer.Serialize(record, Json));
        await writer.FlushAsync();
    }
    private async Task WriteLoopAsync()
    {
        await Restored;
        await foreach (var item in queue.Reader.ReadAllAsync())
        {
            try
            {
                if (item.Clear)
                {
                    await CloseWriterAsync();
                    List<Exception> errors = [];
                    foreach (var file in Files())
                        try { File.Delete(file); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { errors.Add(e); }
                    if (errors.Count > 0) throw new IOException("일부 로그 파일을 삭제하지 못했습니다. 파일 잠금과 접근 권한을 확인하세요.", errors[0]);
                }
                else if (item.Record is { } record) await WriteAsync(record);
                else if (writer is not null) await writer.FlushAsync();
                item.Done?.TrySetResult();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                warning("로그 파일 기록/초기화 실패: " + e.Message);
                try { await CloseWriterAsync(); } catch (Exception closeError) when (closeError is IOException or UnauthorizedAccessException) { }
                item.Done?.TrySetException(e);
            }
        }
        try { await CloseWriterAsync(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { warning("로그 파일 종료 저장 실패: " + e.Message); }
    }
    public async ValueTask DisposeAsync()
    {
        queue.Writer.TryComplete();
        await worker;
    }
}
