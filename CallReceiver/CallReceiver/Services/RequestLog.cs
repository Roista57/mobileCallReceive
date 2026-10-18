using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using System.Windows.Threading;
using CallReceiver.Models;

namespace CallReceiver.Services;

public sealed record LogEntry(DateTimeOffset Time, string Message)
{
    public string Id { get; init; } = Guid.NewGuid().ToString();
    public RequestDiagnostic? Detail { get; init; }
    [JsonIgnore] public string Display => $"{Time:HH:mm:ss}  {Message}";
    [JsonIgnore] public string ExportDisplay => $"{Time:yyyy.MM.dd HH:mm:ss}  {Message}";
}
public sealed class RequestLog : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly Dispatcher? dispatcher;
    private readonly LogFileStore? files;
    private readonly Func<DateTimeOffset> now;
    private readonly List<LogEntry> recent = [];
    private readonly Dictionary<string, string> popupStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> popupOrder = new();
    private bool publishScheduled, clearPending;
    private bool disposed;
    public ObservableCollection<LogEntry> Entries { get; } = [];
    public string? PersistenceWarning { get; private set; }
    public string? LogDirectory => files?.DirectoryPath;
    public long DroppedFileRecords => files?.DroppedRecords ?? 0;
    public event Action<string>? PersistenceFailed;
    public event Action<LogEntry>? Added;
    public event Action? Cleared;
    public event Action<LogEntry>? Updated;

    public RequestLog(Dispatcher? dispatcher = null, string? directory = null, Func<DateTimeOffset>? now = null)
    {
        this.dispatcher = dispatcher; this.now = now ?? (() => DateTimeOffset.Now);
        if (directory is not null) files = new LogFileStore(directory, ReportWarning, this.now);
    }
    internal RequestLog(LogFileStore files) : this() { this.files = files; }
    private void ReportWarning(string text)
    {
        if (PersistenceWarning == text) return;
        PersistenceWarning = text;
        // Errors must never be logged back to the file writer recursively.
        if (dispatcher is not null && !dispatcher.CheckAccess()) dispatcher.BeginInvoke(() => PersistenceFailed?.Invoke(text));
        else PersistenceFailed?.Invoke(text);
    }
    // At most one dispatcher callback retains a notification, rather than a snapshot per request.
    private void Publish(bool clear = false)
    {
        clearPending |= clear;
        void Apply()
        {
            LogEntry[] snapshot;
            bool cleared;
            lock (gate)
            {
                publishScheduled = false;
                snapshot = recent.ToArray();
                cleared = clearPending; clearPending = false;
            }
            var previous = Entries.ToDictionary(e => e.Id);
            Entries.Clear();
            foreach (var entry in snapshot) Entries.Add(entry);
            if (cleared) Cleared?.Invoke();
            foreach (var entry in snapshot)
                if (!previous.TryGetValue(entry.Id, out var old)) Added?.Invoke(entry);
                else if (old != entry) Updated?.Invoke(entry);
        }
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            if (publishScheduled) return;
            publishScheduled = true;
            dispatcher.BeginInvoke(Apply);
        }
        else Apply();
    }
    public async Task InitializeAsync()
    {
        if (files is null) return;
        var restored = await files.Restored;
        lock (gate)
        {
            var merged = recent.Concat(restored).DistinctBy(e => e.Id).Take(200).ToArray();
            recent.Clear(); recent.AddRange(merged);
            Publish();
        }
    }
    public void Add(string text, RequestDiagnostic? detail = null)
    {
        lock (gate)
        {
            if (disposed) return;
            var entry = new LogEntry(now(), text.Replace('\r', ' ').Replace('\n', ' ')) { Detail = detail };
            if (entry.Detail?.EventId is { } id && popupStates.TryGetValue(id, out var state))
                entry = entry with { Detail = entry.Detail with { PopupState = state } };
            recent.Insert(0, entry);
            if (recent.Count > 200) recent.RemoveAt(recent.Count - 1);
            files?.Append(entry);
            Publish();
            System.Diagnostics.Debug.WriteLine(entry.Display);
        }
    }
    public void SetPopupState(string eventId, string state)
    {
        lock (gate)
        {
            if (disposed) return;
            if (!popupStates.ContainsKey(eventId)) popupOrder.Enqueue(eventId);
            popupStates[eventId] = state;
            while (popupOrder.Count > 200) popupStates.Remove(popupOrder.Dequeue());
            files?.Popup(eventId, state);
            for (var i = 0; i < recent.Count; i++)
                if (recent[i].Detail is { } detail && string.Equals(detail.EventId, eventId, StringComparison.OrdinalIgnoreCase))
                    recent[i] = recent[i] with { Detail = detail with { PopupState = state } };
            Publish();
        }
    }
    public IReadOnlyList<LogEntry> Snapshot() { lock (gate) return recent.ToArray(); }
    public Task ClearAsync()
    {
        lock (gate)
        {
            if (disposed) throw new InvalidOperationException("로그 작성기가 종료되었습니다.");
            var deletion = files?.ClearAsync() ?? Task.CompletedTask;
            recent.Clear(); popupStates.Clear(); popupOrder.Clear();
            Publish(clear: true);
            return deletion;
        }
    }
    public Task FlushAsync() { lock (gate) return files?.FlushAsync() ?? Task.CompletedTask; }
    public async ValueTask DisposeAsync()
    {
        lock (gate) { if (disposed) return; disposed = true; }
        if (files is not null) await files.DisposeAsync();
    }
}
