using System.Drawing;
using Forms = System.Windows.Forms;

namespace CallReceiver.Services;

public sealed class TrayService : IDisposable
{
    private readonly Forms.NotifyIcon icon;
    public TrayService(Action show, Action status, Action exit)
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("설정 열기", null, (_, _) => show());
        menu.Items.Add("서버 상태", null, (_, _) => status());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("종료", null, (_, _) => exit());
        icon = new Forms.NotifyIcon { Text = "MCS 전화 수신 알림", Icon = SystemIcons.Information,
            ContextMenuStrip = menu, Visible = true };
        icon.DoubleClick += (_, _) => show();
    }
    public void Update(bool running) => icon.Text = running ? "MCS · HTTP 서버 실행 중" : "MCS · HTTP 서버 중지";
    public void Dispose() { icon.Visible = false; icon.ContextMenuStrip?.Dispose(); icon.Dispose(); }
}
