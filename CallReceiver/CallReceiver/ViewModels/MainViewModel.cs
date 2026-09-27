using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Threading;
using System.Net.Http;
using CallReceiver.Models;
using CallReceiver.Services;

namespace CallReceiver.ViewModels;

public sealed class AsyncCommand(Func<Task> execute, Func<bool> canExecute) : ICommand
{
    public bool CanExecute(object? parameter) => canExecute();
    public async void Execute(object? parameter) => await execute();
    public event EventHandler? CanExecuteChanged;
    public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly SettingsService settingsService;
    private readonly SettingsLocationService locationService;
    private readonly HttpServerService server;
    private readonly EventStore store;
    private readonly NotificationManager notifications;
    private readonly StartupService startup;
    private readonly HttpSelfTest selfTest = new();
    private readonly Dispatcher dispatcher;
    private readonly List<AsyncCommand> commands = [];
    private AppSettings saved;
    private bool busy;
    private string message = "", lastError = "", lastRequest = "없음", popupStatus = "없음";
    public AppSettings Saved => Volatile.Read(ref saved);
    public RequestLog Log { get; }
    public IReadOnlyList<MonitorInfo> Monitors { get; private set; } = [];
    public bool IsBusy { get => busy; private set { busy = value; Raise(); Raise(nameof(IsEditable)); Raise(nameof(CanStartServer)); Raise(nameof(CanStopServer)); commands.ForEach(c => c.Refresh()); } }
    public bool IsEditable => !IsBusy;
    public bool CanStartServer => !IsBusy && !server.IsRunning;
    public bool CanStopServer => !IsBusy && server.IsRunning;
    public string Message { get => message; private set { message = value; Raise(); } }
    public string LastError { get => lastError; private set { lastError = value; Raise(); } }
    public string LastRequest { get => lastRequest; private set { lastRequest = value; Raise(); } }
    public string PopupStatus { get => popupStatus; private set { popupStatus = value; Raise(); } }
    public string ServerStatus => server.IsRunning ? "실행 중" : "중지됨";
    public string SettingsDirectory => settingsService.DirectoryPath;
    public IReadOnlyList<string> TimeFormats { get; } =
        ["yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd tt h:mm:ss", "HH:mm:ss", "tt h:mm"];
    public string TimePreview { get { try { return DateTimeOffset.Now.ToString(TimeFormat); } catch { return "잘못된 시간 형식"; } } }
    public string Listening => server.RunningSettings is { } s
        ? $"{s.ListenAddress}:{s.ListenPort}"
        : "수신 중인 주소 없음";
    public string DirtyText { get { try { return ReadDraft() == Saved ? "" : "저장하지 않은 변경 사항이 있습니다."; } catch { return "입력값을 확인하고 저장하세요."; } } }
    public AsyncCommand SaveCommand { get; }
    public AsyncCommand StartCommand { get; }
    public AsyncCommand StopCommand { get; }
    public AsyncCommand HealthCommand { get; }
    public AsyncCommand PreviewCommand { get; }
    public AsyncCommand RefreshCommand { get; }
    public AsyncCommand ChooseSettingsDirectoryCommand { get; }
    public AsyncCommand ResetSettingsDirectoryCommand { get; }
    public event Action? ServerChanged;

    public MainViewModel(AppSettings initial, SettingsService settingsService, SettingsLocationService locationService, HttpServerService server,
        EventStore store, NotificationManager notifications, StartupService startup, RequestLog log, Dispatcher dispatcher)
    {
        saved = initial; this.settingsService = settingsService; this.locationService = locationService; this.server = server; this.store = store;
        this.notifications = notifications; this.startup = startup; Log = log; this.dispatcher = dispatcher;
        LoadDraft(initial);
        RefreshEnvironment();
        AsyncCommand Command(Func<Task> action)
        {
            var command = new AsyncCommand(() => ExecuteAsync(action), () => !IsBusy);
            commands.Add(command); return command;
        }
        SaveCommand = Command(SaveAsync);
        StartCommand = Command(async () => { EnsureSaved(); await server.StartAsync(Saved); Message = "HTTP 서버를 시작했습니다."; });
        StopCommand = Command(async () => { await server.StopAsync(); Message = "HTTP 수신을 중지했습니다. 접수된 알림은 계속 표시됩니다."; });
        HealthCommand = Command(async () => { EnsureServer(); Message = await selfTest.HealthAsync(Saved); });
        PreviewCommand = Command(() =>
        {
            var value = ReadDraft();
            if (value.Validate() is { } error) throw new ArgumentException(error);
            notifications.Preview(value);
            Message = "현재 입력한 설정으로 위치 테스트를 예약했습니다.";
            return Task.CompletedTask;
        });
        RefreshCommand = Command(() => { RefreshEnvironment(); return Task.CompletedTask; });
        ChooseSettingsDirectoryCommand = Command(async () =>
        {
            using var dialog = new System.Windows.Forms.FolderBrowserDialog
            { Description = "설정과 이벤트 DB를 저장할 빈 폴더를 선택하세요.", SelectedPath = SettingsDirectory };
            if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
            await locationService.ChangeAsync(SettingsDirectory, dialog.SelectedPath);
            Message = "설정 위치를 저장했습니다. 프로그램을 다시 시작하면 적용됩니다.";
        });
        ResetSettingsDirectoryCommand = Command(async () =>
        {
            await locationService.ChangeAsync(SettingsDirectory, locationService.ExecutableDirectory);
            Message = "기본 설정 위치를 저장했습니다. 프로그램을 다시 시작하면 적용됩니다.";
        });
        log.Added += entry => { LastRequest = entry.Display; };
        notifications.Shown += id => dispatcher.BeginInvoke(() => PopupStatus = $"표시 성공 · {id}");
    }

    public void SetMessage(string text) => Message = text;
    public Task RefreshStatusAsync()
    {
        Raise(nameof(ServerStatus)); Raise(nameof(Listening));
        return Task.CompletedTask;
    }
    private void RefreshEnvironment()
    {
        Monitors = MonitorService.List();
        Raise(nameof(Monitors));
    }
    public async Task StartInitialAsync()
    {
        await ExecuteAsync(() => server.StartAsync(Saved));
    }
    private void EnsureSaved()
    {
        if (ReadDraft() != Saved) throw new InvalidOperationException("변경한 설정을 먼저 저장하세요.");
    }
    private void EnsureServer()
    {
        EnsureSaved();
        if (!server.IsRunning) throw new InvalidOperationException("HTTP 서버가 실행 중이 아닙니다.");
    }
    private async Task ExecuteAsync(Func<Task> action)
    {
        if (IsBusy) return;
        IsBusy = true;
        try { await action(); LastError = ""; }
        catch (HttpRequestException e) { LastError = Message = e.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden
            ? "인증 오류: API Key를 확인하세요." : $"HTTP 오류/연결 실패 ({e.StatusCode?.ToString() ?? "서버·포트 확인"})"; }
        catch (OperationCanceledException) { LastError = Message = "Timeout: 요청 시간이 초과되었습니다."; }
        catch (Exception e)
        {
            LastError = Message = e is ArgumentException or InvalidOperationException or FormatException
                ? e.Message : "작업 실패: 포트 충돌, 파일 접근 권한 또는 서버 상태를 확인하세요.";
        }
        finally { IsBusy = false; Raise(nameof(ServerStatus)); Raise(nameof(Listening)); Raise(nameof(DirtyText)); Raise(nameof(CanStartServer)); Raise(nameof(CanStopServer)); ServerChanged?.Invoke(); }
    }
    private async Task SaveAsync()
    {
        var next = ReadDraft();
        if (next.Validate() is { } error) throw new ArgumentException(error);
        var old = Saved;
        var oldStartup = startup.Read();
        var restart = server.IsRunning && (old.ListenAddress != next.ListenAddress ||
            old.ListenPort != next.ListenPort || old.ApiPath != next.ApiPath);
        try
        {
            if (restart) { await server.StopAsync(); await server.StartAsync(next); }
            startup.Apply(next.StartWithWindows);
            await settingsService.SaveAsync(next);
            Volatile.Write(ref saved, next);
            Message = "설정을 저장했습니다. 알림 설정은 다음 팝업부터 적용됩니다.";
            Raise(nameof(DirtyText));
        }
        catch
        {
            startup.Restore(oldStartup);
            if (restart)
            {
                try { await server.StopAsync(); await server.StartAsync(old); }
                catch { Log.Add("이전 서버 설정 복구 실패. 현재 서버 상태를 확인하세요."); }
            }
            throw;
        }
    }
    private string _ListenAddress = "127.0.0.1";
    public string ListenAddress { get => _ListenAddress; set { _ListenAddress = value; Raise(); Raise(nameof(DirtyText)); } }
    private string _ListenPort = "18080";
    public string ListenPort { get => _ListenPort; set { _ListenPort = value; Raise(); Raise(nameof(DirtyText)); } }
    private string _ApiPath = "/api/call";
    public string ApiPath { get => _ApiPath; set { _ApiPath = value; Raise(); Raise(nameof(DirtyText)); } }
    private string _Monitor = "";
    public string Monitor { get => _Monitor; set { _Monitor = value; Raise(); Raise(nameof(DirtyText)); } }
    private string _PopupX = "0";
    public string PopupX { get => _PopupX; set { _PopupX = value; Raise(); Raise(nameof(DirtyText)); } }
    private string _PopupY = "0";
    public string PopupY { get => _PopupY; set { _PopupY = value; Raise(); Raise(nameof(DirtyText)); } }
    private string _PopupWidth = "350";
    public string PopupWidth { get => _PopupWidth; set { _PopupWidth = value; Raise(); Raise(nameof(DirtyText)); } }
    private string _PopupHeight = "140";
    public string PopupHeight { get => _PopupHeight; set { _PopupHeight = value; Raise(); Raise(nameof(DirtyText)); } }
    private string _DisplayDurationSeconds = "5";
    public string DisplayDurationSeconds { get => _DisplayDurationSeconds; set { _DisplayDurationSeconds = value; Raise(); Raise(nameof(DirtyText)); } }
    private string _TimeFormat = "yyyy-MM-dd HH:mm:ss";
    public string TimeFormat { get => _TimeFormat; set { _TimeFormat = value; Raise(); Raise(nameof(TimePreview)); Raise(nameof(DirtyText)); } }
    private bool _TopMost = true;
    public bool TopMost { get => _TopMost; set { _TopMost = value; Raise(); Raise(nameof(DirtyText)); } }
    private bool _PlaySound = true;
    public bool PlaySound { get => _PlaySound; set { _PlaySound = value; Raise(); Raise(nameof(DirtyText)); } }
    private bool _StartWithWindows = false;
    public bool StartWithWindows { get => _StartWithWindows; set { _StartWithWindows = value; Raise(); Raise(nameof(DirtyText)); } }
    private bool _MinimizeToTray = true;
    public bool MinimizeToTray { get => _MinimizeToTray; set { _MinimizeToTray = value; Raise(); Raise(nameof(DirtyText)); } }
    private void LoadDraft(AppSettings s)
    {
        ListenAddress = s.ListenAddress;
        ListenPort = s.ListenPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
        ApiPath = s.ApiPath;
        Monitor = s.Monitor;
        PopupX = s.PopupX.ToString(System.Globalization.CultureInfo.InvariantCulture);
        PopupY = s.PopupY.ToString(System.Globalization.CultureInfo.InvariantCulture);
        PopupWidth = s.PopupWidth.ToString(System.Globalization.CultureInfo.InvariantCulture);
        PopupHeight = s.PopupHeight.ToString(System.Globalization.CultureInfo.InvariantCulture);
        DisplayDurationSeconds = s.DisplayDurationSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        TimeFormat = s.TimeFormat;
        TopMost = s.TopMost;
        PlaySound = s.PlaySound;
        StartWithWindows = s.StartWithWindows;
        MinimizeToTray = s.MinimizeToTray;
    }
    private AppSettings ReadDraft()
    {
        double Number(string s) => double.Parse(s, System.Globalization.CultureInfo.InvariantCulture);
        return new AppSettings {
            ListenAddress = ListenAddress,
            ListenPort = int.Parse(ListenPort),
            ApiPath = ApiPath,
            Monitor = Monitor,
            PopupX = Number(PopupX),
            PopupY = Number(PopupY),
            PopupWidth = Number(PopupWidth),
            PopupHeight = Number(PopupHeight),
            DisplayDurationSeconds = Number(DisplayDurationSeconds),
            TimeFormat = TimeFormat,
            TopMost = TopMost,
            PlaySound = PlaySound,
            StartWithWindows = StartWithWindows,
            MinimizeToTray = MinimizeToTray,
        };
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
