using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace HiveMotion;

public enum OverlayState
{
    Hidden,
    TaskGrid
}

public partial class App : System.Windows.Application
{
    private const string MutexName = "HiveMotion_SingleInstance";
    private System.Threading.Mutex? _singleInstanceMutex;
    private bool _ownsMutex;

    private GlobalKeyboardHook? _keyboardHook;
    private OverlayWindow? _overlayWindow;
    private TrayIconManager? _trayIconManager;
    private AutoStartManager? _autoStartManager;
    private WindowScanner? _windowScanner;
    private CellAssigner? _cellAssigner;

    private OverlayState _state = OverlayState.Hidden;
    private IReadOnlyList<HiveCell> _currentCells = new List<HiveCell>();
    private IntPtr _previousForeground;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstanceMutex = new System.Threading.Mutex(true, MutexName, out _ownsMutex);
        if (!_ownsMutex)
        {
            Shutdown();
            return;
        }

        var config = AppConfig.Default;
        _windowScanner = new WindowScanner(config.PriorityProcessNames);
        _cellAssigner = new CellAssigner(config);
        _overlayWindow = new OverlayWindow();
        _overlayWindow.CellChosen += (_, cell) => ActivateCell(cell);
        _overlayWindow.CloseRequested += (_, _) => CloseOverlay(restoreFocus: true);
        _overlayWindow.Hide();

        _autoStartManager = new AutoStartManager();
        _trayIconManager = new TrayIconManager(_autoStartManager);
        _trayIconManager.ExitRequested += (_, _) => Shutdown();

        _keyboardHook = new GlobalKeyboardHook();
        _keyboardHook.WinTabPressed += OnWinTabPressed;
        _keyboardHook.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _keyboardHook?.Dispose();
        _trayIconManager?.Dispose();
        _overlayWindow?.Close();
        if (_ownsMutex)
        {
            _singleInstanceMutex?.ReleaseMutex();
        }
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }

    private void OnWinTabPressed(object? sender, EventArgs e)
    {
        // Never block the hook callback: marshal to the UI thread immediately.
        Dispatcher.BeginInvoke(() =>
        {
            if (_state == OverlayState.Hidden)
                OpenTaskGrid();
            else
                CloseOverlay(restoreFocus: true);
        });
    }

    private void OpenTaskGrid()
    {
        _previousForeground = NativeMethods.GetForegroundWindow();

        var windows = _windowScanner!.Scan();
        var cells = _cellAssigner!.Assign(windows);
        _currentCells = cells;
        _state = OverlayState.TaskGrid;
        _overlayWindow!.ShowTaskGrid(cells);
    }

    private void ActivateCell(HiveCell cell)
    {
        // A deliberate switch, not a cancel: focus goes to the chosen window, not back.
        _previousForeground = IntPtr.Zero;

        if (cell.IsRunning)
        {
            WindowManager.ActivateWindow(cell.WindowHandle);
        }
        else if (cell.Preset != null)
        {
            WindowManager.Launch(cell.Preset.ExecutablePath);
        }
        CloseOverlay(restoreFocus: false);
    }

    private void CloseOverlay(bool restoreFocus)
    {
        _state = OverlayState.Hidden;
        _overlayWindow!.HideOverlay();
        _currentCells = new List<HiveCell>();

        if (restoreFocus && _previousForeground != IntPtr.Zero)
        {
            var target = _previousForeground;
            _previousForeground = IntPtr.Zero;
            Dispatcher.BeginInvoke(() => WindowManager.ActivateWindow(target));
        }
        else
        {
            _previousForeground = IntPtr.Zero;
        }
    }
}
