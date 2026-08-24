using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace NetScope.App.Services;

public sealed class TrayService : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Drawing.Icon? _applicationIcon;

    public TrayService(Action show, Action togglePause, Action refresh, Action exit)
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("打开 NetScope", null, (_, _) => show());
        menu.Items.Add("暂停 / 恢复刷新", null, (_, _) => togglePause());
        menu.Items.Add("立即刷新", null, (_, _) => refresh());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => exit());
        var executablePath = Environment.ProcessPath;
        _applicationIcon = string.IsNullOrWhiteSpace(executablePath) ? null : Drawing.Icon.ExtractAssociatedIcon(executablePath);
        _icon = new Forms.NotifyIcon
        {
            Text = "NetScope · 端口与网络诊断",
            Icon = _applicationIcon ?? Drawing.SystemIcons.Application,
            ContextMenuStrip = menu,
            Visible = true
        };
        _icon.DoubleClick += (_, _) => show();
    }

    public void ShowInfo(string text) => _icon.ShowBalloonTip(1500, "NetScope", text, Forms.ToolTipIcon.Info);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _applicationIcon?.Dispose();
    }
}
