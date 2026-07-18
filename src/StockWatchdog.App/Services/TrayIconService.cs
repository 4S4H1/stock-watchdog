using DrawingIcon = System.Drawing.Icon;
using FormsContextMenuStrip = System.Windows.Forms.ContextMenuStrip;
using FormsNotifyIcon = System.Windows.Forms.NotifyIcon;
using FormsToolStripMenuItem = System.Windows.Forms.ToolStripMenuItem;

namespace StockWatchdog.App.Services;

public sealed class TrayIconService : IDisposable
{
    private readonly FormsNotifyIcon _notifyIcon;

    public TrayIconService()
    {
        var menu = new FormsContextMenuStrip();
        var showHide = new FormsToolStripMenuItem("显示/隐藏");
        var settings = new FormsToolStripMenuItem("设置");
        var exit = new FormsToolStripMenuItem("退出监控");
        showHide.Click += (_, _) => ShowHideRequested?.Invoke(this, EventArgs.Empty);
        settings.Click += (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty);
        exit.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);
        menu.Items.AddRange([showHide, settings, new System.Windows.Forms.ToolStripSeparator(), exit]);

        _notifyIcon = new FormsNotifyIcon
        {
            Icon = DrawingIcon.ExtractAssociatedIcon(Environment.ProcessPath ?? string.Empty)
                   ?? System.Drawing.SystemIcons.Information,
            Text = "项目跟踪",
            Visible = true,
            ContextMenuStrip = menu
        };
        _notifyIcon.DoubleClick += (_, _) => ShowHideRequested?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? ShowHideRequested;

    public event EventHandler? SettingsRequested;

    public event EventHandler? ExitRequested;

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }
}
