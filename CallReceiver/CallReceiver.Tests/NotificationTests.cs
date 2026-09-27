using System.Collections.Concurrent;
using CallReceiver.Models;
using CallReceiver.Services;
using Xunit;

namespace CallReceiver.Tests;

public class NotificationTests
{
    private sealed class Presenter : IPopupPresenter
    {
        public ConcurrentQueue<string> Displayed { get; } = new();
        public int Active, MaxActive, Failures;
        public async Task ShowAsync(CallEvent value, AppSettings settings, Func<Task> onShown, CancellationToken token)
        {
            if (Interlocked.CompareExchange(ref Failures, 0, 1) == 1) throw new InvalidOperationException();
            var active = Interlocked.Increment(ref Active);
            MaxActive = Math.Max(MaxActive, active);
            try
            {
                Displayed.Enqueue(value.EventId);
                await onShown();
                await Task.Delay(50, token);
            }
            finally { Interlocked.Decrement(ref Active); }
        }
    }
    [Fact] public async Task QueueIsSequentialAndAcknowledgesOnlyShownEvents()
    {
        using var dir = new TestDirectory();
        var store = new EventStore(Path.Combine(dir.Path, "events.db")); await store.InitializeAsync();
        var events = Enumerable.Range(0, 4).Select(_ => CallEvent.Test()).ToArray();
        foreach (var value in events) await store.AcceptAsync(value);
        var presenter = new Presenter();
        await using var manager = new NotificationManager(store, presenter, () => new AppSettings(), new RequestLog());
        manager.Start();
        using var timeout = new CancellationTokenSource(5000);
        while (await store.PendingCountAsync() != 0) await Task.Delay(20, timeout.Token);
        Assert.Equal(events.Select(e => e.EventId), presenter.Displayed);
        Assert.Equal(1, presenter.MaxActive);
    }
    [Fact] public async Task PresentationFailureRetainsEventWithoutBusyLoop()
    {
        using var dir = new TestDirectory();
        var store = new EventStore(Path.Combine(dir.Path, "events.db")); await store.InitializeAsync();
        var value = CallEvent.Test(); await store.AcceptAsync(value);
        var presenter = new Presenter { Failures = 1 };
        await using var manager = new NotificationManager(store, presenter, () => new AppSettings(), new RequestLog());
        manager.Start();
        await Task.Delay(300);
        Assert.Equal(1, await store.PendingCountAsync());
        Assert.Empty(presenter.Displayed);
    }
}
