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
    private RequestLog? requestLog;
    private MainViewModel? model;
    private SettingsLocationService? settingsLocation;
    private EventStore? eventStore;
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
            settingsLocation = location;
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
            var primary = MonitorService.Primary();
            var defaults = new AppSettings { ListenAddress = NetworkAddresses.DefaultAddress(), Monitor = primary.Id,
                PopupX = Math.Max(0, primary.Width / primary.ScaleX - 370),
                PopupY = Math.Max(0, primary.Height / primary.ScaleY - 160) };
            InitializedStorage storage;
            var originalDirectory = directory;
            while (true)
            {
                try { storage = await StartupStorage.OpenAsync(directory, defaults); break; }
                catch (StorageInitializationException error)
                {
                    var recovery = new StorageRecoveryWindow(error.Message, directory);
                    if (recovery.ShowDialog() != true || recovery.SelectedDirectory is null) { Shutdown(); return; }
                    directory = Path.GetFullPath(recovery.SelectedDirectory);
                }
            }
            string? recoveryWarning = null;
            if (directory != originalDirectory)
            {
                if (overrideDirectory is not null)
                    recoveryWarning = $"이번 실행은 선택한 폴더를 사용합니다. 다음 실행에도 --data-dir \"{directory}\"를 지정하세요.";
                else
                {
                    try { await location.ActivateDirectoryAsync(directory); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                    {
                        recoveryWarning = $"선택한 폴더로 실행하지만 설정 위치 파일을 저장하지 못했습니다: {error.Message}\n" +
                            $"다음 실행에는 --data-dir \"{directory}\"를 지정하거나 실행 폴더 쓰기 권한을 확인하세요.";
                    }
                }
            }
            var settings = storage.Settings;
            var loaded = storage.Loaded;
            var store = storage.Store;
            eventStore = store;
            var log = new RequestLog(Dispatcher, directory);
            requestLog = log;
            await log.InitializeAsync();
            server = new HttpServerService(store, log);
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
            if (log.PersistenceWarning is { } logWarning) model.SetMessage(logWarning);
            if (recoveryWarning is not null) model.SetMessage(recoveryWarning);
            if (recoveryWarning is not null) MessageBox.Show(recoveryWarning, "선택한 설정 위치 안내");
            if (e.Args.Contains("--settings")) ShowSettings();
            timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            timer.Tick += async (_, _) => await model.RefreshStatusAsync();
            timer.Start();
        }
        catch (Exception error)
        {
            MessageBox.Show($"앱 초기화에 실패했습니다: {error.Message}", "MCS");
            await ExitAsync();
        }
    }
    private async Task ExitAsync()
    {
        if (exiting) return;
        exiting = true;
        timer?.Stop();
        Exception? failure = null;
        try
        {
            if (model is not null) await model.PrepareShutdownAsync();
            try { if (server is not null) await server.StopAsync(); }
            finally { if (notifications is not null) await notifications.DisposeAsync(); }
        }
        catch (Exception error) { failure = error; }
        finally
        {
            try
            {
                if (requestLog is not null) await requestLog.DisposeAsync();
                if (failure is null && settingsLocation is not null && eventStore is not null && model is not null)
                    await settingsLocation.CompletePendingAsync(model.Saved, eventStore);
            }
            catch (Exception error) { failure ??= error; }
            finally
            {
                if (failure is not null)
                    MessageBox.Show("종료 정리 또는 설정 위치 이전을 완료하지 못했습니다. 기존 데이터와 설정 위치를 유지합니다.\n" + failure.Message, "MCS");
                tray?.Dispose();
                if (window is not null) { window.AllowClose = true; window.Close(); }
                Shutdown();
            }
        }
    }
    protected override void OnExit(ExitEventArgs e)
    {
        instance?.Dispose();
        base.OnExit(e);
    }
}
