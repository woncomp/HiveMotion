using System;
using System.Drawing;
using System.Windows.Forms;

namespace HiveMotion;

public sealed class TrayIconManager : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _contextMenu;
    private bool _disposed;

    public event EventHandler? ExitRequested;

    public TrayIconManager(AutoStartManager autoStartManager)
    {
        _contextMenu = new ContextMenuStrip();

        var autoStartItem = new ToolStripMenuItem("开机自启动")
        {
            Checked = autoStartManager.IsAutoStartEnabled(),
            CheckOnClick = true
        };
        autoStartItem.Click += (_, _) =>
        {
            autoStartItem.Checked = !autoStartItem.Checked;
            if (autoStartItem.Checked)
                autoStartManager.EnableAutoStart();
            else
                autoStartManager.DisableAutoStart();
        };
        _contextMenu.Items.Add(autoStartItem);

        _contextMenu.Items.Add(new ToolStripSeparator());

        var exitItem = new ToolStripMenuItem("退出");
        exitItem.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);
        _contextMenu.Items.Add(exitItem);

        _notifyIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "HiveMotion",
            Visible = true,
            ContextMenuStrip = _contextMenu
        };
        _notifyIcon.DoubleClick += (_, _) =>
        {
            // no-op in MVP
        };
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _contextMenu.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
