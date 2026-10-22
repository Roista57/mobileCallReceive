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

    private sealed class FailingStartup : IStartupService
    {
        public string? Read() => "old registration";
        public void Apply(bool enabled) => throw new UnauthorizedAccessException("registration blocked");
        public void Restore(string? value) => throw new UnauthorizedAccessException("restore blocked");
    }

    [Fact] public Task RegistryRestoreFailureStillRestoresServerAndShutdownWaitsForCommands() => OnSta(async () =>
    {
        await using var fixture = new HttpFixture(); await fixture.StartAsync();
        await using var freePort = new HttpFixture();
        var initial = fixture.Settings;
        var settings = new SettingsService(fixture.Directory.Path); await settings.SaveAsync(initial);
        await using var manager = new NotificationManager(fixture.Store, new WpfPopupPresenter(Dispatcher.CurrentDispatcher, fixture.Log),
            () => initial, fixture.Log);
        var model = new MainViewModel(initial, settings, new SettingsLocationService(fixture.Directory.Path),
            fixture.Server, fixture.Store, manager, new FailingStartup(), fixture.Log, Dispatcher.CurrentDispatcher,
            () => initial.ListenAddress);
        model.ListenPort = freePort.Settings.ListenPort.ToString();
        model.SaveCommand.Execute(null);
        await WaitUntil(() => !model.IsBusy);
        Assert.Contains("자동 실행 복구 실패", model.Message);
        Assert.Contains("이전 서버 설정 복구 완료", model.Message);
        Assert.Equal(initial, fixture.Server.RunningSettings);
        Assert.Equal(initial, (await settings.LoadAsync(initial)).Settings);
        Assert.Contains("정상", await new HttpSelfTest().HealthAsync(initial));
        await model.PrepareShutdownAsync();
        Assert.False(model.SaveCommand.CanExecute(null));
        Assert.False(model.CanStartServer);
        Assert.False(model.CanStopServer);
    });

    [Fact] public Task StorageRecoveryUsesDefaultControlsAndDoesNotDeleteData() => OnSta(() =>
    {
        using var directory = new TestDirectory();
        var path = System.IO.Path.Combine(directory.Path, "events.sqlite3"); File.WriteAllText(path, "keep");
        var window = new StorageRecoveryWindow("이벤트 DB 초기화 실패", directory.Path);
        Assert.Contains("저장소 복구", window.Title);
        Assert.Null(window.SelectedDirectory);
        window.Close();
        Assert.Equal("keep", File.ReadAllText(path));
        return Task.CompletedTask;
    });

    [Fact] public Task RequestDetailWindowsAreNonModalAndKeepSnapshots() => OnSta(async () =>
    {
        var log = new RequestLog(Dispatcher.CurrentDispatcher);
        var diagnostic = new RequestDiagnostic { EventId = Guid.NewGuid().ToString(), RequestBody = "{\"phoneNumber\":\"01012345678\"}",
            RemoteAddress = "192.168.0.20:50000", LocalAddress = "192.168.0.50:18080" };
        log.Add("POST /api/call 200 01012345678", diagnostic);
        var owner = new Window(); owner.Show();
        var detail = new RequestDetailWindow(log.Snapshot()[0], log) { Owner = owner };
        detail.Show();
        Assert.True(owner.IsEnabled);
        Assert.Contains("192.168.0.20:50000 → 192.168.0.50:18080", detail.DetailText.Text);
        Assert.Contains("01012345678", detail.DetailText.Text);
        log.SetPopupState(diagnostic.EventId!, "표시 시작");
        Assert.Contains("표시 시작", detail.DetailText.Text);
        await log.ClearAsync();
        Assert.Contains("01012345678", detail.DetailText.Text);
        detail.Close(); owner.Close();
        await Task.CompletedTask;
    });

    [Fact] public Task HiddenLinesAndIndependentAlignment() => OnSta(() =>
    {
        foreach (var phoneSize in new[] { 0, 22 })
        foreach (var timeSize in new[] { 0, 22 })
        {
            var popup = new CallPopupWindow(CallEvent.Test(), new AppSettings
            {
                PhoneFontSize = phoneSize, TimeFontSize = timeSize,
                PhoneAlignment = "Center", TimeAlignment = "Right", PhonePrefix = "", TimePrefix = "시각 >"
            }, MonitorService.Primary());
            var phone = (System.Windows.Controls.TextBlock)popup.FindName("NumberText");
            var time = (System.Windows.Controls.TextBlock)popup.FindName("TimeText");
            var grid = (System.Windows.Controls.Grid)popup.FindName("BodyGrid");
            Assert.Equal(phoneSize == 0 ? Visibility.Collapsed : Visibility.Visible, phone.Visibility);
            Assert.Equal(timeSize == 0 ? Visibility.Collapsed : Visibility.Visible, time.Visibility);
            if (phoneSize != 0) Assert.Equal(TextAlignment.Center, phone.TextAlignment);
            if (timeSize != 0) Assert.Equal(TextAlignment.Right, time.TextAlignment);
            Assert.Equal("010-1234-5678", phone.Text);
            Assert.StartsWith("시각 > ", time.Text);
            Assert.Equal(1 + (phoneSize > 0 ? 2 : 0) + (timeSize > 0 ? 2 : 0), grid.RowDefinitions.Count);
            Assert.Null(popup.FindName("ServerText"));
            popup.Close();
        }
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task FullWpfHttpPathPopupFocusTimingAndHiddenWindow(bool isTest) => OnSta(async () =>
    {
        var log = new RequestLog(Dispatcher.CurrentDispatcher);
        await using var f = new HttpFixture(log);
        await f.StartAsync();
        var monitor = MonitorService.Primary();
        var initial = f.Settings with { Monitor = monitor.Id, PopupX = 20, PopupY = 20,
            DisplayDurationSeconds = 1, PlaySound = false, PhoneFontSize = 18, PhoneFontBold = false,
            TimeFontSize = 27, TimeFontBold = true };
        var settingsService = new SettingsService(f.Directory.Path);
        var locationService = new SettingsLocationService(f.Directory.Path, f.Directory.Path);
        await settingsService.SaveAsync(initial);
        var exportedLog = System.IO.Path.Combine(f.Directory.Path, "exported-log.txt");
        MainViewModel? model = null;
        await using var manager = new NotificationManager(f.Store,
            new WpfPopupPresenter(Dispatcher.CurrentDispatcher, log), () => model?.Saved ?? initial, log);
        model = new MainViewModel(initial, settingsService, locationService, f.Server, f.Store, manager,
            new StartupService("MCS_UiTest_" + Guid.NewGuid()), log, Dispatcher.CurrentDispatcher,
            () => initial.ListenAddress, (_, _) => true, () => exportedLog);
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
            Assert.Equal("전화 수신 설정", window.Title);
            Assert.Equal(620, window.Height);
            Assert.Equal(500, window.MinHeight);
            Assert.Equal("현재 주소", ((System.Windows.Controls.Label)window.FindName("CurrentAddressLabel")).Content);
            Assert.NotNull(window.FindName("ResetSettingsButton"));
            Assert.NotNull(window.FindName("ClearLogButton"));
            Assert.NotNull(window.FindName("ExportLogButton"));
            var phoneWeightBox = (System.Windows.Controls.ComboBox)window.FindName("PhoneFontWeightBox");
            var timeWeightBox = (System.Windows.Controls.ComboBox)window.FindName("TimeFontWeightBox");
            Assert.Equal("Normal", phoneWeightBox.SelectedValue);
            Assert.Equal("Bold", timeWeightBox.SelectedValue);
            Assert.Equal(System.Windows.HorizontalAlignment.Left, phoneWeightBox.HorizontalAlignment);
            Assert.Equal(System.Windows.HorizontalAlignment.Left, timeWeightBox.HorizontalAlignment);
            var phoneSizeBox = (System.Windows.Controls.TextBox)window.FindName("PhoneFontSizeBox");
            var timeSizeBox = (System.Windows.Controls.TextBox)window.FindName("TimeFontSizeBox");
            Assert.Equal(3, phoneSizeBox.MaxLength);
            Assert.Equal(3, timeSizeBox.MaxLength);
            Assert.Equal(56, phoneSizeBox.Width);
            Assert.Equal(56, timeSizeBox.Width);
            manager.Start();
            var foreground = GetForegroundWindow();
            model.HealthCommand.Execute(null);
            await WaitUntil(() => !model.IsBusy);
            Assert.Contains("HTTP 서버 정상", model.Message);
            var stopwatch = Stopwatch.StartNew();
            var localReceived = DateTimeOffset.Now.Date.AddHours(isTest ? 9 : 14).AddMinutes(30).AddSeconds(25);
            var received = CallEvent.Test() with { IsTest = isTest, ReceivedAt = new DateTimeOffset(localReceived) };
            Assert.Equal(received.EventId, await new HttpSelfTest().SendAsync(initial, received));
            await WaitUntil(() => OpenWindows<CallPopupWindow>().Length == 1);
            var popup = Assert.Single(OpenWindows<CallPopupWindow>());
            var expectedTitle = $"{initial.NotificationText} {(isTest ? "09:30:25" : "14:30:25")} {received.PhoneNumber}";
            Assert.Equal(expectedTitle, popup.Title);
            Assert.Equal(expectedTitle, ((System.Windows.Controls.TextBlock)popup.FindName("TitleText")).Text);
            var numberText = (System.Windows.Controls.TextBlock)popup.FindName("NumberText");
            var timeText = (System.Windows.Controls.TextBlock)popup.FindName("TimeText");
            Assert.Equal("전화번호: 010-1234-5678", numberText.Text);
            Assert.Equal($"수신시간: {received.ReceivedAt.ToLocalTime():yyyy.MM.dd HH:mm:ss}", timeText.Text);
            Assert.Equal(18, numberText.FontSize);
            Assert.Equal(System.Windows.FontWeights.Normal, numberText.FontWeight);
            Assert.Equal(27, timeText.FontSize);
            Assert.Equal(System.Windows.FontWeights.Bold, timeText.FontWeight);
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
            model.TestPhoneNumber = "0212345678";
            model.TitleTimeFormat = "'TITLE' HH:mm";
            model.BodyTimeFormat = "'BODY' yyyy/MM/dd";
            model.ListenAddress = "203.0.113.10";
            model.PopupX = "999999"; model.PopupY = "-50"; model.TopMost = false;
            model.PreviewCommand.Execute(null);
            await WaitUntil(() => OpenWindows<CallPopupWindow>().Length == 1);
            popup = Assert.Single(OpenWindows<CallPopupWindow>());
            Assert.False(popup.Topmost);
            Assert.Equal("전화번호: 02-1234-5678", ((System.Windows.Controls.TextBlock)popup.FindName("NumberText")).Text);
            Assert.EndsWith(" 0212345678", popup.Title);
            Assert.Equal(popup.Title, ((System.Windows.Controls.TextBlock)popup.FindName("TitleText")).Text);
            Assert.Equal("0212345678", model.TestPhoneNumber);
            Assert.Contains("TITLE", popup.Title);
            Assert.Contains("BODY", ((System.Windows.Controls.TextBlock)popup.FindName("TimeText")).Text);
            Assert.False(model.TitleChoices1.Single(c => c.Id == "Time").IsEnabled);
            model.TitleItem1 = "Time";
            Assert.Equal("Notice", model.TitleItem1);
            model.TitleItem1 = "None";
            model.TitleItem2 = "None";
            model.TitleItem3 = "None";
            Assert.All(model.TitleChoices1, c => Assert.True(c.IsEnabled));
            model.PreviewCommand.Execute(null);
            await WaitUntil(() => OpenWindows<CallPopupWindow>().Length == 1 && OpenWindows<CallPopupWindow>()[0].Title == "");
            popup = Assert.Single(OpenWindows<CallPopupWindow>());
            Assert.Equal("", ((System.Windows.Controls.TextBlock)popup.FindName("TitleText")).Text);
            model.TitleItem1 = "Notice"; model.TitleItem2 = "Time"; model.TitleItem3 = "Phone";
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
                Assert.NotEmpty(model.Message);
                Assert.Equal(initial.ListenPort, model.Saved.ListenPort);
                Assert.Contains("HTTP 서버 정상", await new HttpSelfTest().HealthAsync(initial));
                Assert.Equal(initial.ListenPort, (await settingsService.LoadAsync(initial)).Settings.ListenPort);
            }
            window.Show();
            window.UpdateLayout();
            model.StopCommand.Execute(null);
            await WaitUntil(() => !model.IsBusy);
            var logCountBeforeReset = log.Entries.Count;
            model.ResetSettingsCommand.Execute(null);
            await WaitUntil(() => !model.IsBusy);
            Assert.False(f.Server.IsRunning);
            Assert.Equal(18080, model.Saved.ListenPort);
            Assert.Equal("/api/call", model.Saved.ApiPath);
            Assert.Equal(logCountBeforeReset, log.Entries.Count);
            Assert.True(File.Exists(System.IO.Path.Combine(f.Directory.Path, "events.db")));

            model.ExportLogCommand.Execute(null);
            await WaitUntil(() => !model.IsBusy);
            Assert.True(File.Exists(exportedLog));
            var exportedText = await File.ReadAllTextAsync(exportedLog, System.Text.Encoding.UTF8);
            Assert.Matches(@"\d{4}\.\d{2}\.\d{2} \d{2}:\d{2}:\d{2}  ", exportedText);
            model.ClearLogCommand.Execute(null);
            await WaitUntil(() => !model.IsBusy);
            Assert.Empty(log.Entries);

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
