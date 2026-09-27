using System.Windows.Threading;
using CallReceiver.Models;
using CallReceiver.Views;

namespace CallReceiver.Services;

public sealed class WpfPopupPresenter(Dispatcher dispatcher, RequestLog log) : IPopupPresenter
{
    public Task ShowAsync(CallEvent value, AppSettings settings, Func<Task> onShown, CancellationToken token) =>
        dispatcher.InvokeAsync(async () =>
        {
            token.ThrowIfCancellationRequested();
            var monitor = MonitorService.List().FirstOrDefault(m => m.Id == settings.Monitor);
            if (monitor is null)
            {
                monitor = MonitorService.Primary();
                log.Add("선택한 모니터가 없어 기본 모니터에 알림을 표시합니다.");
            }
            var window = new CallPopupWindow(value, settings, monitor);
            try
            {
                window.Show();
                await onShown();
                if (settings.PlaySound) System.Media.SystemSounds.Asterisk.Play();
                await window.LifetimeAsync(settings.DisplayDurationSeconds, token);
            }
            finally { if (window.IsVisible) window.Close(); }
        }).Task.Unwrap();
}
