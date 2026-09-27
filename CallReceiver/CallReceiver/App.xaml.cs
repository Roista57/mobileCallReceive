using System.Windows;
using System.Windows.Threading;
using CallReceiver.Models;
using CallReceiver.Services;
using CallReceiver.ViewModels;
using CallReceiver.Views;

namespace CallReceiver;

public partial class App : Application
{
    private SingleInstance? instance;
    private HttpServerService? server;
    private NotificationManager? notifications;
    private TrayService? tray;
    private MainWindow? window;
    private DispatcherTimer? timer;
    private bool exiting;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            string? overrideDirectory = null;
            var dataIndex = Array.IndexOf(e.Args, "--data-dir");
            if (dataIndex >= 0)
            {
                if (dataIndex + 1 >= e.Args.Length) throw new ArgumentException("--data-dir 경로가 필요합니다.");
                overrideDirectory = Path.GetFullPath(e.Args[dataIndex + 1]);
            }
            var location = new SettingsLocationService(AppContext.BaseDirectory, overrideDirectory);
            var resolved = await location.ResolveAsync();
            var directory = resolved.Directory;
            // An explicit test data directory isolates both data and the single-instance lock.
            var suffix = dataIndex < 0 ? "" : "-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(directory.ToUpperInvariant())))[..16];
            instance = new SingleInstance(suffix);
            if (!instance.IsPrimary)
            {
                try { await instance.NotifyPrimaryAsync(); }
                catch { MessageBox.Show("이미 실행 중입니다. 시스템 트레이에서 설정을 열어 주세요.", "MCS"); }
                Shutdown(); return;
            }
            var settings = new SettingsService(directory);
            var primary = MonitorService.Primary();
            var defaults = new AppSettings { Monitor = primary.Id,
                PopupX = Math.Max(0, primary.Width / primary.ScaleX - 370),
                PopupY = Math.Max(0, primary.Height / primary.ScaleY - 160) };
            var loaded = await settings.LoadAsync(defaults);
            if (loaded.FirstRun && loaded.Warning is null) await settings.SaveAsync(loaded.Settings);
            var store = new EventStore(Path.Combine(directory, "events.sqlite3"));
            await store.InitializeAsync();
            var log = new RequestLog(Dispatcher);
            server = new HttpServerService(store, log);
            MainViewModel? model = null;
            notifications = new NotificationManager(store, new WpfPopupPresenter(Dispatcher, log),
                () => model?.Saved ?? loaded.Settings, log);
            model = new MainViewModel(loaded.Settings, settings, location, server, store, notifications, new StartupService(), log, Dispatcher);
            window = new MainWindow(model, ExitAsync);
            MainWindow = window;
            void ShowSettings() => Dispatcher.Invoke(() => { window.Show(); window.WindowState = WindowState.Normal; window.Activate(); });
            tray = new TrayService(ShowSettings, ShowSettings, () => _ = ExitAsync());
            instance.Listen(ShowSettings);
            model.ServerChanged += () => tray.Update(server.IsRunning);
            server.EventAccepted += notifications.Wake;
            notifications.Start();
            await model.StartInitialAsync();
            if (loaded.Warning is { } warning) model.SetMessage(warning);
            if (resolved.Warning is { } locationWarning) model.SetMessage(locationWarning);
            if (loaded.FirstRun || loaded.Warning is not null || !server.IsRunning || e.Args.Contains("--settings")) ShowSettings();
            timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            timer.Tick += async (_, _) => await model.RefreshStatusAsync();
            timer.Start();
        }
        catch (Exception)
        {
            MessageBox.Show("앱 초기화에 실패했습니다. 설정 폴더 접근 권한과 저장 공간을 확인하세요.", "MCS");
            await ExitAsync();
        }
    }
    private async Task ExitAsync()
    {
        if (exiting) return;
        exiting = true;
        timer?.Stop();
        try
        {
            if (server is not null) await server.StopAsync();
            if (notifications is not null) await notifications.DisposeAsync();
        }
        finally
        {
            tray?.Dispose();
            if (window is not null) { window.AllowClose = true; window.Close(); }
            Shutdown();
        }
    }
    protected override void OnExit(ExitEventArgs e)
    {
        instance?.Dispose();
        base.OnExit(e);
    }
}
