using System.Collections.Concurrent;
using CallReceiver.Models;

namespace CallReceiver.Services;

public interface IPopupPresenter
{
    Task ShowAsync(CallEvent value, AppSettings settings, Func<Task> onShown, CancellationToken token);
}

public sealed class NotificationManager(EventStore store, IPopupPresenter presenter,
    Func<AppSettings> settings, RequestLog log) : IAsyncDisposable
{
    private readonly ConcurrentQueue<(CallEvent Event, AppSettings Settings)> previews = new();
    private readonly SemaphoreSlim signal = new(0, 1);
    private readonly CancellationTokenSource stop = new();
    private readonly object popupGate = new();
    private CancellationTokenSource? activePopup;
    private Task? loop;
    public event Action<string>? Shown;
    public void Start() { loop ??= Task.Run(() => RunAsync(stop.Token)); }
    public void Wake()
    {
        lock (popupGate) activePopup?.Cancel();
        try { signal.Release(); } catch (SemaphoreFullException) { }
    }
    public void Preview(AppSettings value)
    {
        if (previews.Count >= 10) throw new InvalidOperationException("위치 테스트가 대기 중입니다. 잠시 기다려 주세요.");
        previews.Enqueue((CallEvent.Test() with { PhoneNumber = value.TestPhoneNumber }, value));
        Wake();
    }
    private async Task RunAsync(CancellationToken token)
    {
        var lastPrune = DateTimeOffset.UtcNow;
        while (!token.IsCancellationRequested)
        {
            try
            {
                if (DateTimeOffset.UtcNow - lastPrune > TimeSpan.FromDays(1))
                {
                    await store.PruneAsync();
                    lastPrune = DateTimeOffset.UtcNow;
                }
                if (previews.TryDequeue(out var preview))
                {
                    await ShowAsync(preview.Event, preview.Settings, () => Task.CompletedTask, token);
                    continue;
                }
                var value = await store.NextAsync();
                if (value is null) { await signal.WaitAsync(TimeSpan.FromSeconds(2), token); continue; }
                await ShowAsync(value, settings(), async () =>
                {
                    await store.MarkShownAsync(value.EventId);
                    log.Add("알림 표시 완료");
                    Shown?.Invoke(value.EventId);
                }, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (OperationCanceledException) { }
            catch (Exception)
            {
                log.Add("알림 표시/저장 오류. 5초 뒤 재시도합니다.");
                try { await Task.Delay(TimeSpan.FromSeconds(5), token); }
                catch (OperationCanceledException) { break; }
            }
        }
    }
    private async Task ShowAsync(CallEvent value, AppSettings valueSettings, Func<Task> onShown, CancellationToken token)
    {
        using var popup = CancellationTokenSource.CreateLinkedTokenSource(token);
        lock (popupGate) activePopup = popup;
        try { await presenter.ShowAsync(value, valueSettings, onShown, popup.Token); }
        finally
        {
            lock (popupGate)
                if (ReferenceEquals(activePopup, popup)) activePopup = null;
        }
    }
    public async ValueTask DisposeAsync()
    {
        await stop.CancelAsync();
        if (loop is not null) await loop;
        stop.Dispose();
        signal.Dispose();
    }
}
