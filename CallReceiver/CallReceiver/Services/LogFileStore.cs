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
    internal const int DefaultCapacity = 256;
    private sealed record Work(StoredLog? Record, bool Clear, TaskCompletionSource? Done);
    private readonly Channel<Work> queue = Channel.CreateUnbounded<Work>(new UnboundedChannelOptions
        { SingleReader = true, AllowSynchronousContinuations = false });
    private readonly Action<string> warning;
    private readonly Func<DateTimeOffset> now;
    private readonly Task worker;
    private readonly SemaphoreSlim recordSlots;
    private readonly Func<StoredLog, Task> writeRecord;
    private int pendingRecords, maxPendingRecords, overflowNotified;
    private long droppedRecords, reportedDrops;
    public int PendingRecords => Volatile.Read(ref pendingRecords);
    public int MaxPendingRecords => Volatile.Read(ref maxPendingRecords);
    public long DroppedRecords => Interlocked.Read(ref droppedRecords);
    private StreamWriter? writer;
    private string? openPath;
    private static readonly JsonSerializerOptions Json = new(JsonDefaults.Options) { WriteIndented = false };
    public string DirectoryPath { get; }
    public Task<IReadOnlyList<LogEntry>> Restored { get; }

    public LogFileStore(string directory, Action<string> warning, Func<DateTimeOffset> now,
        int capacity = DefaultCapacity, Func<StoredLog, Task>? writeRecord = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        DirectoryPath = Path.Combine(Path.GetFullPath(directory), "logs");
        this.warning = warning; this.now = now;
        recordSlots = new SemaphoreSlim(capacity, capacity);
        this.writeRecord = writeRecord ?? WriteAsync;
        Restored = Task.Run(ReadRecent);
        worker = Task.Run(WriteLoopAsync);
    }
    public void Append(LogEntry entry) => EnqueueRecord(new(1, "entry", now(), entry));
    public void Popup(string eventId, string state) => EnqueueRecord(new(1, "popup", now(), EventId: eventId, PopupState: state));
    private void EnqueueRecord(StoredLog record)
    {
        // Bound payloads, including the in-flight write. Control barriers remain ordered and never drop.
        if (!recordSlots.Wait(0))
        {
            Interlocked.Increment(ref droppedRecords);
            if (Interlocked.Exchange(ref overflowNotified, 1) == 0)
                warning("로그 저장 대기열이 가득 찼습니다. 초과한 기록은 파일에 저장되지 않으며 메모리 최근 목록과 HTTP 처리는 유지됩니다.");
            return;
        }
        var pending = Interlocked.Increment(ref pendingRecords);
        int previous;
        do { previous = maxPendingRecords; if (pending <= previous) break; }
        while (Interlocked.CompareExchange(ref maxPendingRecords, pending, previous) != previous);
        if (!queue.Writer.TryWrite(new(record, false, null))) ReleaseRecord();
    }
    private void ReleaseRecord() { Interlocked.Decrement(ref pendingRecords); recordSlots.Release(); }
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
                    if (record.Entry?.Detail is { } detail)
                    {
                        var normalized = detail.Normalize(out var repaired);
                        if (repaired) warning("손상된 로그의 일부 상세 항목을 안전한 기본값으로 보정했습니다.");
                        record = record with { Entry = record.Entry with { Detail = normalized } };
                    }
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
                else if (item.Record is { } record) await writeRecord(record);
                else if (writer is not null) await writer.FlushAsync();
                if (item.Done is not null)
                {
                    var dropped = DroppedRecords;
                    if (dropped != reportedDrops)
                    {
                        reportedDrops = dropped;
                        warning($"로그 대기열 초과로 파일에 저장하지 못한 기록: 누적 {dropped}건. HTTP 접수 결과와는 별개입니다.");
                    }
                    Interlocked.Exchange(ref overflowNotified, 0);
                }
                item.Done?.TrySetResult();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                warning("로그 파일 기록/초기화 실패: " + e.Message);
                try { await CloseWriterAsync(); } catch (Exception closeError) when (closeError is IOException or UnauthorizedAccessException) { }
                item.Done?.TrySetException(e);
            }
            finally { if (item.Record is not null) ReleaseRecord(); }
        }
        try { await CloseWriterAsync(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { warning("로그 파일 종료 저장 실패: " + e.Message); }
    }
    public async ValueTask DisposeAsync()
    {
        queue.Writer.TryComplete();
        await worker;
        recordSlots.Dispose();
    }
}
