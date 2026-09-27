using System.Runtime.InteropServices;
using System.Windows;
using Forms = System.Windows.Forms;

namespace CallReceiver.Services;

public sealed record MonitorInfo(string Id, string Label, int Left, int Top, int Width, int Height,
    double ScaleX, double ScaleY)
{
    public override string ToString() => Label;
}
public sealed record PopupBounds(int Left, int Top, int Width, int Height);

public static class MonitorService
{
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(Point point, uint flags);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint x, out uint y);
    public static IReadOnlyList<MonitorInfo> List() => Forms.Screen.AllScreens.Select((s, i) =>
    {
        var handle = MonitorFromPoint(new Point { X = s.Bounds.Left + 1, Y = s.Bounds.Top + 1 }, 2);
        GetDpiForMonitor(handle, 0, out var x, out var y);
        return new MonitorInfo(s.DeviceName, $"{i + 1}: {s.DeviceName}{(s.Primary ? " (기본)" : "")}",
            s.WorkingArea.Left, s.WorkingArea.Top, s.WorkingArea.Width, s.WorkingArea.Height,
            x > 0 ? x / 96.0 : 1, y > 0 ? y / 96.0 : 1);
    }).ToArray();
    public static MonitorInfo Primary() => List().First(m => m.Id == Forms.Screen.PrimaryScreen!.DeviceName);
    public static PopupBounds Clamp(MonitorInfo m, double x, double y, double width, double height)
    {
        var w = Math.Min(m.Width, Math.Max(1, (int)Math.Round(width * m.ScaleX)));
        var h = Math.Min(m.Height, Math.Max(1, (int)Math.Round(height * m.ScaleY)));
        var left = m.Left + (int)Math.Clamp(Math.Round(x * m.ScaleX), 0, m.Width - w);
        var top = m.Top + (int)Math.Clamp(Math.Round(y * m.ScaleY), 0, m.Height - h);
        return new(left, top, w, h);
    }
}
