using System;

namespace HiveMotion;

/// <summary>
/// Owns the tray presence of the app. The actual icon and Fluent-style context
/// menu live in <see cref="TrayHostWindow"/> (WPF-UI tray:NotifyIcon); this class
/// only forwards its events so the wiring in App stays unchanged.
/// </summary>
public sealed class TrayIconManager : IDisposable
{
    private readonly TrayHostWindow _hostWindow;
    private bool _disposed;

    public event EventHandler? ExitRequested;
    /// <summary>"Open HiveMotion" menu item: show the overlay.</summary>
    public event EventHandler? ShowRequested;
    /// <summary>Manage menu item or tray-icon left double-click: open the manage window.</summary>
    public event EventHandler? ManageRequested;

    public TrayIconManager()
    {
        _hostWindow = new TrayHostWindow();
        _hostWindow.ShowRequested += (_, _) => ShowRequested?.Invoke(this, EventArgs.Empty);
        _hostWindow.ManageRequested += (_, _) => ManageRequested?.Invoke(this, EventArgs.Empty);
        _hostWindow.ExitRequested += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);
        // WPF-UI resolves the tray icon's parent window from Application.MainWindow.
        // OverlayWindow is instantiated first and would otherwise claim that role
        // despite never being shown, which makes tray registration silently fail.
        System.Windows.Application.Current.MainWindow = _hostWindow;
        // The NotifyIcon registers with the shell during its first render pass.
        _hostWindow.Show();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _hostWindow.DisposeTrayIcon();
        _hostWindow.Close();
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
