using System.Collections.ObjectModel;
using System.Windows.Threading;

namespace CallReceiver.Services;

public sealed record LogEntry(DateTimeOffset Time, string Message)
{
    public string Display => $"{Time:HH:mm:ss}  {Message}";
}
public sealed class RequestLog
{
    private readonly Dispatcher? dispatcher;
    public ObservableCollection<LogEntry> Entries { get; } = [];
    public event Action<LogEntry>? Added;
    public RequestLog(Dispatcher? dispatcher = null) { this.dispatcher = dispatcher; }
    public void Add(string text)
    {
        var entry = new LogEntry(DateTimeOffset.Now, text.Replace('\r', ' ').Replace('\n', ' '));
        void Apply()
        {
            Entries.Insert(0, entry);
            while (Entries.Count > 200) Entries.RemoveAt(Entries.Count - 1);
            Added?.Invoke(entry);
        }
        if (dispatcher is not null && !dispatcher.CheckAccess()) dispatcher.BeginInvoke(Apply);
        else lock (Entries) Apply();
        System.Diagnostics.Debug.WriteLine(entry.Display);
    }
}
