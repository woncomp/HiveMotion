using System;
using System.Windows;

namespace HiveMotion;

/// <summary>
/// Hidden host for the WPF-UI tray icon and its Fluent context menu.
/// Menu item texts bind through {l:Loc}, so they re-resolve automatically
/// when the UI culture changes.
/// </summary>
public partial class TrayHostWindow : Window
{
    /// <summary>"Open hive" menu item: show the overlay.</summary>
    public event EventHandler? ShowRequested;
    /// <summary>"Manage center" menu item or tray-icon left double-click.</summary>
    public event EventHandler? ManageRequested;
    /// <summary>"Exit" menu item.</summary>
    public event EventHandler? ExitRequested;

    public TrayHostWindow()
    {
        InitializeComponent();
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        // The WPF-UI NotifyIcon self-registers during OnRender; retry once here in
        // case that pass ran before a usable parent window source was available.
        if (!TrayIcon.IsRegistered)
            TrayIcon.Register();
        Logger.Info($"Tray host rendered; tray icon registered={TrayIcon.IsRegistered}.");
    }

    /// <summary>Removes the icon from the notification area.</summary>
    public void DisposeTrayIcon() => TrayIcon.Dispose();

    private void OnOpenHiveClick(object sender, RoutedEventArgs e) =>
        ShowRequested?.Invoke(this, EventArgs.Empty);

    private void OnManageClick(object sender, RoutedEventArgs e) =>
        ManageRequested?.Invoke(this, EventArgs.Empty);

    private void OnExitClick(object sender, RoutedEventArgs e) =>
        ExitRequested?.Invoke(this, EventArgs.Empty);

    private void OnTrayLeftDoubleClick(object sender, RoutedEventArgs e) =>
        ManageRequested?.Invoke(this, EventArgs.Empty);
}
