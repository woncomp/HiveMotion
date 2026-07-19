using System;
using System.Drawing;
using System.Windows.Forms;
using HiveMotion.Localization;

namespace HiveMotion;

public sealed class TrayIconManager : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _contextMenu;
    private readonly ToolStripMenuItem _showItem;
    private readonly ToolStripMenuItem _manageItem;
    private readonly ToolStripMenuItem _autoStartItem;
    private readonly ToolStripMenuItem _exitItem;
    private bool _disposed;

    public event EventHandler? ExitRequested;
    /// <summary>Left-click on the tray icon: show the overlay.</summary>
    public event EventHandler? ShowRequested;
    /// <summary>"管理中心…" menu item: open the manage window.</summary>
    public event EventHandler? ManageRequested;

    public TrayIconManager(AutoStartManager autoStartManager)
    {
        _contextMenu = new ContextMenuStrip();

        _showItem = new ToolStripMenuItem();
        _showItem.Click += (_, _) => ShowRequested?.Invoke(this, EventArgs.Empty);
        _contextMenu.Items.Add(_showItem);

        _manageItem = new ToolStripMenuItem();
        _manageItem.Click += (_, _) => ManageRequested?.Invoke(this, EventArgs.Empty);
        _contextMenu.Items.Add(_manageItem);

        _contextMenu.Items.Add(new ToolStripSeparator());

        _autoStartItem = new ToolStripMenuItem
        {
            Checked = autoStartManager.IsAutoStartEnabled(),
            CheckOnClick = true
        };
        _autoStartItem.Click += (_, _) =>
        {
            _autoStartItem.Checked = !_autoStartItem.Checked;
            if (_autoStartItem.Checked)
                autoStartManager.EnableAutoStart();
            else
                autoStartManager.DisableAutoStart();
        };
        _contextMenu.Items.Add(_autoStartItem);

        _contextMenu.Items.Add(new ToolStripSeparator());

        _exitItem = new ToolStripMenuItem();
        _exitItem.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);
        _contextMenu.Items.Add(_exitItem);

        _notifyIcon = new NotifyIcon
        {
            Icon = LoadAppIcon(),
            Text = "HiveMotion",
            Visible = true,
            ContextMenuStrip = _contextMenu
        };
        _notifyIcon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                ShowRequested?.Invoke(this, EventArgs.Empty);
        };

        ApplyLocalizedStrings();
        LocalizationManager.Instance.CultureChanged += OnCultureChanged;
    }

    private void OnCultureChanged(object? sender, EventArgs e) => ApplyLocalizedStrings();

    private void ApplyLocalizedStrings()
    {
        _showItem.Text = Loc.Get("Tray_OpenHive");
        _manageItem.Text = Loc.Get("Tray_Manage");
        _autoStartItem.Text = Loc.Get("Tray_AutoStart");
        _exitItem.Text = Loc.Get("Tray_Exit");
    }

    private static System.Drawing.Icon LoadAppIcon()
    {
        try
        {
            var path = Environment.ProcessPath;
            if (path != null)
            {
                var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
                if (icon != null)
                    return icon;
            }
        }
        catch
        {
            // fall through to the stock icon
        }
        return SystemIcons.Application;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        LocalizationManager.Instance.CultureChanged -= OnCultureChanged;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _contextMenu.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
