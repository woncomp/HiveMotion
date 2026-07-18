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
        _overlayWindow.CloseRequested += (_, _) => CloseOverlay();
        _overlayWindow.Hide();

        _autoStartManager = new AutoStartManager();
        _trayIconManager = new TrayIconManager(_autoStartManager);
        _trayIconManager.ExitRequested += (_, _) => Shutdown();

        _keyboardHook = new GlobalKeyboardHook();
        _keyboardHook.WinTabPressed += OnWinTabPressed;
        _keyboardHook.EscapePressed += OnEscapePressed;
        _keyboardHook.CellKeyPressed += OnCellKeyPressed;
        _keyboardHook.SearchRequested += OnSearchRequested;
        _keyboardHook.SearchCharTyped += OnSearchCharTyped;
        _keyboardHook.SearchBackspace += (_, _) => _overlayWindow.SearchBackspace();
        _keyboardHook.SearchSubmit += (_, _) => _overlayWindow.SubmitSearch();
        _keyboardHook.SearchArrow += (_, delta) => _overlayWindow.MoveSearchHighlight(delta);
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
        if (_state == OverlayState.Hidden)
            OpenTaskGrid();
        else
            CloseOverlay();
    }

    private void OpenTaskGrid()
    {
        IReadOnlyList<HiveCell> cells = Array.Empty<HiveCell>();
        Dispatcher.Invoke(() =>
        {
            var windows = _windowScanner!.Scan();
            cells = _cellAssigner!.Assign(windows);
            _currentCells = cells;
        });

        _state = OverlayState.TaskGrid;
        _keyboardHook!.OverlayOpen = true;
        _keyboardHook.Searching = false;
        _overlayWindow!.ShowTaskGrid(cells);
    }

    private void OnEscapePressed(object? sender, EventArgs e)
    {
        if (_state != OverlayState.TaskGrid)
            return;

        if (_keyboardHook!.Searching)
        {
            _keyboardHook.Searching = false;
            _overlayWindow!.ExitSearch();
        }
        else
        {
            CloseOverlay();
        }
    }

    private void OnCellKeyPressed(object? sender, char key)
    {
        if (_state != OverlayState.TaskGrid)
            return;

        var cell = _currentCells.FirstOrDefault(c => c.Letter == key);
        if (cell != null)
            ActivateCell(cell);
    }

    private void OnSearchRequested(object? sender, EventArgs e)
    {
        if (_state != OverlayState.TaskGrid || _keyboardHook!.Searching)
            return;
        _keyboardHook.Searching = true;
        _overlayWindow!.EnterSearch();
    }

    private void OnSearchCharTyped(object? sender, char c)
    {
        if (_state != OverlayState.TaskGrid)
            return;
        _overlayWindow!.AppendSearchChar(c);
    }

    private void ActivateCell(HiveCell cell)
    {
        if (cell.IsRunning)
        {
            WindowManager.ActivateWindow(cell.WindowHandle);
        }
        else if (cell.Preset != null)
        {
            WindowManager.Launch(cell.Preset.ExecutablePath);
        }
        CloseOverlay();
    }

    private void CloseOverlay()
    {
        _state = OverlayState.Hidden;
        if (_keyboardHook != null)
        {
            _keyboardHook.OverlayOpen = false;
            _keyboardHook.Searching = false;
        }
        _overlayWindow!.HideOverlay();
        _currentCells = new List<HiveCell>();
    }
}
