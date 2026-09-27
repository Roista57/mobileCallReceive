using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CallReceiver.Models;
using CallReceiver.Services;
using CallReceiver.ViewModels;
using CallReceiver.Views;
using Xunit;

namespace CallReceiver.Tests;

[CollectionDefinition("Desktop", DisableParallelization = true)]
public sealed class DesktopCollection { }

[Collection("Desktop")]
[Trait("Category", "Desktop")]
public sealed class WpfUiTests
{
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    private static T[] OpenWindows<T>() where T : Window => PresentationSource.CurrentSources
        .OfType<HwndSource>().Select(s => s.RootVisual).OfType<T>().Where(w => w.IsVisible).ToArray();
    private static Task OnSta(Func<Task> action)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () =>
            {
                try { await action(); done.SetResult(); }
                catch (Exception error) { done.SetException(error); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }
    private static async Task WaitUntil(Func<bool> ready)
    {
        using var timeout = new CancellationTokenSource(8000);
        while (!ready()) await Task.Delay(20, timeout.Token);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task FullWpfHttpPathPopupFocusTimingAndHiddenWindow(bool isTest) => OnSta(async () =>
    {
        await using var f = new HttpFixture();
        await f.StartAsync();
        var monitor = MonitorService.Primary();
        var initial = f.Settings with { Monitor = monitor.Id, PopupX = 20, PopupY = 20,
            DisplayDurationSeconds = 1, PlaySound = false };
        var settingsService = new SettingsService(f.Directory.Path);
        var locationService = new SettingsLocationService(f.Directory.Path, f.Directory.Path);
        await settingsService.SaveAsync(initial);
        var log = new RequestLog(Dispatcher.CurrentDispatcher);
        MainViewModel? model = null;
        await using var manager = new NotificationManager(f.Store,
            new WpfPopupPresenter(Dispatcher.CurrentDispatcher, log), () => model?.Saved ?? initial, log);
        model = new MainViewModel(initial, settingsService, locationService, f.Server, f.Store, manager,
            new StartupService("MCS_UiTest_" + Guid.NewGuid()), log, Dispatcher.CurrentDispatcher);
        var window = new MainWindow(model, () => Task.CompletedTask);
        using var tray = new TrayService(() => window.Show(), () => window.Show(), () => { });
        f.Server.EventAccepted += manager.Wake;
        try
        {
            window.Show();
            window.Activate();
            await Task.Delay(150);
            Assert.True(window.IsVisible);
            Assert.Equal(model, window.DataContext);
            manager.Start();
            var foreground = GetForegroundWindow();
            model.HealthCommand.Execute(null);
            await WaitUntil(() => !model.IsBusy);
            Assert.Contains("HTTP 서버 정상", model.Message);
            var stopwatch = Stopwatch.StartNew();
            var received = CallEvent.Test() with { IsTest = isTest };
            Assert.Equal(received.EventId, await new HttpSelfTest().SendAsync(initial, received));
            await WaitUntil(() => OpenWindows<CallPopupWindow>().Length == 1);
            var popup = Assert.Single(OpenWindows<CallPopupWindow>());
            var expectedTitle = $"{received.ReceivedAt.ToLocalTime().ToString(initial.TimeFormat)} {received.PhoneNumber}";
            Assert.Equal(expectedTitle, popup.Title);
            Assert.Equal(expectedTitle, ((System.Windows.Controls.TextBlock)popup.FindName("TitleText")).Text);
            Assert.False(popup.ShowInTaskbar);
            Assert.False(popup.ShowActivated);
            Assert.True(popup.Topmost);
            Assert.Equal(foreground, GetForegroundWindow());
            await WaitUntil(() => !model.IsBusy && model.PopupStatus.Contains("표시 성공"));
            GetWindowRect(new WindowInteropHelper(popup).Handle, out var rectangle);
            var expected = MonitorService.Clamp(monitor, 20, 20, 350, 140);
            Assert.InRange(rectangle.Left, expected.Left - 2, expected.Left + 2);
            Assert.InRange(rectangle.Top, expected.Top - 2, expected.Top + 2);
            await WaitUntil(() => OpenWindows<CallPopupWindow>().Length == 0);
            Assert.InRange(stopwatch.Elapsed.TotalSeconds, 0.9, 5);
            Assert.Equal(0, await f.Store.PendingCountAsync());

            // A location preview uses the draft values, stays on-screen, and does not go through HTTP.
            model.PopupX = "999999"; model.PopupY = "-50"; model.TopMost = false;
            model.PreviewCommand.Execute(null);
            await WaitUntil(() => OpenWindows<CallPopupWindow>().Length == 1);
            popup = Assert.Single(OpenWindows<CallPopupWindow>());
            Assert.False(popup.Topmost);
            GetWindowRect(new WindowInteropHelper(popup).Handle, out rectangle);
            Assert.True(rectangle.Left >= monitor.Left && rectangle.Right <= monitor.Left + monitor.Width + 2);
            Assert.InRange(rectangle.Top, monitor.Top - 2, monitor.Top + 2);
            await WaitUntil(() => OpenWindows<CallPopupWindow>().Length == 0);

            window.Close(); // Closing the settings window must only hide it.
            Assert.False(window.IsVisible);
            Assert.True(f.Server.IsRunning);
            var hiddenEvent = CallEvent.Test();
            await new HttpSelfTest().SendAsync(initial, hiddenEvent);
            await WaitUntil(() => OpenWindows<CallPopupWindow>().Length == 1);
            Assert.False(window.IsVisible);
            await WaitUntil(() => OpenWindows<CallPopupWindow>().Length == 0);

            // A failed live port change restores both the listener and the saved configuration.
            await using (var occupied = new HttpFixture())
            {
                await occupied.StartAsync();
                model.ListenPort = occupied.Settings.ListenPort.ToString();
                model.SaveCommand.Execute(null);
                await WaitUntil(() => !model.IsBusy);
                Assert.NotEmpty(model.LastError);
                Assert.Equal(initial.ListenPort, model.Saved.ListenPort);
                Assert.Contains("HTTP 서버 정상", await new HttpSelfTest().HealthAsync(initial));
                Assert.Equal(initial.ListenPort, (await settingsService.LoadAsync(initial)).Settings.ListenPort);
            }
            window.Show();
            window.UpdateLayout();
            var image = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            image.Render(window);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            Directory.CreateDirectory("TestResults");
            using var output = File.Create("TestResults/wpf-settings.png");
            encoder.Save(output);
        }
        finally { window.AllowClose = true; window.Close(); }
    });
}
