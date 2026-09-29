using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using CallReceiver.Models;
using CallReceiver.Services;

namespace CallReceiver.Views;

public partial class CallPopupWindow : Window
{
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr h, int i, IntPtr value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int hgt, uint flags);
    private readonly PopupBounds bounds;
    private readonly TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public CallPopupWindow(CallEvent value, AppSettings settings, MonitorInfo monitor)
    {
        InitializeComponent();
        Width = settings.PopupWidth; Height = settings.PopupHeight; Topmost = settings.TopMost;
        bounds = MonitorService.Clamp(monitor, settings.PopupX, settings.PopupY, settings.PopupWidth, settings.PopupHeight);
        var number = PhoneText.Format(value.PhoneNumber);
        var time = value.ReceivedAt.ToLocalTime().ToString("yyyy.MM.dd HH:mm:ss");
        TitleText.Text = $"{settings.NotificationText} {number} {value.ReceivedAt.ToLocalTime():HH:mm:ss}";
        Title = TitleText.Text;
        NumberText.Text = $"전화번호: {number}";
        TimeText.Text = $"수신시간: {time}";
        NumberText.FontSize = settings.PhoneFontSize;
        NumberText.FontWeight = settings.PhoneFontBold ? FontWeights.Bold : FontWeights.Normal;
        TimeText.FontSize = settings.TimeFontSize;
        TimeText.FontWeight = settings.TimeFontBold ? FontWeights.Bold : FontWeights.Normal;
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            SetWindowLongPtr(handle, -20, new IntPtr(GetWindowLongPtr(handle, -20).ToInt64() | 0x08000000 | 0x80));
            Place();
        };
        Loaded += (_, _) => Place();
        Closed += (_, _) => closed.TrySetResult();
    }
    private void Place() => SetWindowPos(new WindowInteropHelper(this).Handle,
        new IntPtr(Topmost ? -1 : -2), bounds.Left, bounds.Top, bounds.Width, bounds.Height, 0x0010);
    private void CloseClick(object sender, RoutedEventArgs e) => Close();
    public async Task LifetimeAsync(double seconds, CancellationToken token)
    {
        using var registration = token.Register(() => Dispatcher.BeginInvoke(() => { if (IsVisible) Close(); }));
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150)));
        await Task.WhenAny(closed.Task, Task.Delay(TimeSpan.FromSeconds(seconds), token));
        if (IsVisible && !token.IsCancellationRequested)
        {
            BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(150)));
            await Task.WhenAny(closed.Task, Task.Delay(150, token));
        }
        if (IsVisible) Close();
        token.ThrowIfCancellationRequested();
    }
}
