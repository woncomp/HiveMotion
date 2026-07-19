using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using HiveMotion.Localization;

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
    private PinStore? _pinStore;
    private HistoryStore? _historyStore;
    private SettingsStore? _settingsStore;
    private ManageWindow? _manageWindow;
    private string _activeHotkeyJson = string.Empty;

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

        _pinStore = new PinStore();
        _historyStore = new HistoryStore();
        _settingsStore = new SettingsStore();
        LocalizationManager.Instance.ApplyLanguageSetting(_settingsStore.Settings.Language);
        _windowScanner = new WindowScanner(_settingsStore.Settings.PriorityProcessNames);
        _cellAssigner = new CellAssigner(_pinStore.Pins);
        _overlayWindow = new OverlayWindow();
        _overlayWindow.CellChosen += (_, cell) => ActivateCell(cell);
        _overlayWindow.CloseRequested += (_, _) => CloseOverlay(restoreFocus: true);
        _overlayWindow.PinToggleRequested += (_, cell) => TogglePin(cell);
        _overlayWindow.RevealRequested += (_, cell) => RevealCell(cell);
        _overlayWindow.CopyCommandRequested += (_, cell) => CopyCellCommandLine(cell);
        _overlayWindow.Deactivated += (_, _) =>
        {
            // Clicking elsewhere dismisses the launcher (standard launcher behavior);
            // without this the topmost grid would linger visible but unfocused.
            if (_state == OverlayState.TaskGrid)
                CloseOverlay(restoreFocus: false);
        };
        _overlayWindow.Hide();

        _autoStartManager = new AutoStartManager();
        _trayIconManager = new TrayIconManager(_autoStartManager);
        _trayIconManager.ExitRequested += (_, _) => Shutdown();
        _trayIconManager.ManageRequested += (_, _) => Dispatcher.BeginInvoke(ShowManageWindow);
        _trayIconManager.ShowRequested += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            if (_state == OverlayState.Hidden)
                OpenTaskGrid();
            else
                CloseOverlay(restoreFocus: true);
        });

        _keyboardHook = BuildKeyboardHook();
        _keyboardHook.Start();
    }

    private GlobalKeyboardHook BuildKeyboardHook()
    {
        var settings = _settingsStore!.Settings;
        _activeHotkeyJson = System.Text.Json.JsonSerializer.Serialize(settings.Hotkeys);
        var hook = new GlobalKeyboardHook(settings.Hotkeys)
        {
            PassThroughOnSecondPress = settings.SecondPressPassthrough
        };
        hook.HotkeyOpenRequested += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            try
            {
                OpenTaskGrid();
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
            }
        });
        hook.HotkeyPassthrough += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            // The combo went to the system (Task View & co.); the native UI takes over.
            CloseOverlay(restoreFocus: false);
        });
        return hook;
    }

    /// <summary>Applies edited hotkey settings: rebuilds the hook only when combos changed.</summary>
    private void ApplyHotkeySettings()
    {
        var settings = _settingsStore!.Settings;
        if (_keyboardHook != null)
            _keyboardHook.PassThroughOnSecondPress = settings.SecondPressPassthrough;

        if (System.Text.Json.JsonSerializer.Serialize(settings.Hotkeys) == _activeHotkeyJson)
            return;

        var oldHook = _keyboardHook;
        _keyboardHook = BuildKeyboardHook();
        _keyboardHook.IsOverlayOpen = _state == OverlayState.TaskGrid;
        _keyboardHook.Start();
        oldHook?.Dispose();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _keyboardHook?.Dispose();
        _trayIconManager?.Dispose();
        _manageWindow?.Close();
        _overlayWindow?.Close();
        if (_ownsMutex)
        {
            _singleInstanceMutex?.ReleaseMutex();
        }
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }

    private void OpenTaskGrid()
    {
        _previousForeground = NativeMethods.GetForegroundWindow();

        var windows = _windowScanner!.Scan();
        _historyStore!.RecordScan(windows);
        var cells = _cellAssigner!.Assign(windows);
        _currentCells = cells;
        _state = OverlayState.TaskGrid;
        _keyboardHook!.IsOverlayOpen = true;
        _overlayWindow!.ShowTaskGrid(cells);
    }

    /// <summary>Re-scans and rebuilds the grid in place after a pin/unpin, keeping the overlay open.</summary>
    private void RefreshTaskGrid()
    {
        try
        {
            var windows = _windowScanner!.Scan();
            _historyStore!.RecordScan(windows);
            var cells = _cellAssigner!.Assign(windows);
            _currentCells = cells;
            _overlayWindow!.UpdateCells(cells);
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
        }
    }

    /// <summary>Manage center is a singleton normal window; reopening just brings it forward.</summary>
    private void ShowManageWindow()
    {
        if (_manageWindow == null)
        {
            _manageWindow = new ManageWindow(_pinStore!, _historyStore!, _settingsStore!,
                _autoStartManager!, _windowScanner!, ApplyHotkeySettings);
            _manageWindow.Closed += (_, _) => _manageWindow = null;
            _manageWindow.Show();
        }
        else
        {
            if (_manageWindow.WindowState == WindowState.Minimized)
                _manageWindow.WindowState = WindowState.Normal;
            _manageWindow.Activate();
        }
    }

    /// <summary>
    /// Ctrl+P on a cell. Pinned cells (running or not) ask for removal; running unpinned
    /// cells capture their process identity (same program, same arguments) as a new pin.
    /// </summary>
    private void TogglePin(HiveCell cell)
    {
        if (_state != OverlayState.TaskGrid)
            return;

        if (cell.Pin is { } pin)
        {
            _overlayWindow!.ShowConfirm(
                Loc.Format("App_UnpinMessage", pin.Key, pin.DisplayName, pin.CommandLine),
                Loc.Get("App_UnpinConfirm"),
                () =>
                {
                    _pinStore!.Remove(pin.Key);
                    RefreshTaskGrid();
                });
            return;
        }

        if (!cell.IsRunning)
            return;

        // UWP apps all share ApplicationFrameHost.exe; that identity cannot be relaunched.
        if (IsUwpCell(cell))
        {
            _overlayWindow!.ShowConfirm(Loc.Get("App_UwpNotPinnable"), Loc.Get("Common_Ok"), null);
            return;
        }

        string? executablePath = ProcessIdentity.TryGetImagePath(cell.ProcessId);
        if (executablePath == null)
        {
            _overlayWindow!.ShowConfirm(Loc.Get("App_CannotReadPath"), Loc.Get("Common_Ok"), null);
            return;
        }

        // Arguments and working directory live in the PEB; elevated processes refuse the
        // read and degrade to an exe-only pin that matches any arguments.
        string arguments = string.Empty;
        string workingDirectory = string.Empty;
        if (ProcessIdentity.TryReadProcessParameters(cell.ProcessId, out string? commandLine, out string? currentDirectory))
        {
            arguments = ProcessIdentity.ExtractArguments(commandLine);
            workingDirectory = currentDirectory ?? string.Empty;
        }

        var existing = _pinStore!.FindByIdentity(executablePath, arguments);
        if (existing != null && existing.Key != cell.Letter)
        {
            _overlayWindow!.ShowConfirm(
                Loc.Format("App_MovePinMessage", existing.DisplayName, existing.Key, cell.Letter),
                Loc.Get("App_MovePinConfirm"),
                () =>
                {
                    _pinStore.Remove(existing.Key);
                    existing.Key = cell.Letter;
                    _pinStore.Set(existing);
                    RefreshTaskGrid();
                });
            return;
        }

        _pinStore.Set(new PinnedApp
        {
            Key = cell.Letter,
            ProcessName = cell.ProcessName,
            ExecutablePath = executablePath,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            DisplayName = cell.AppName
        });
        RefreshTaskGrid();
    }

    /// <summary>UWP windows all belong to ApplicationFrameHost.exe; that path is not the app's.</summary>
    private static bool IsUwpCell(HiveCell cell) =>
        cell.ProcessName.Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase);

    /// <summary>Ctrl+R in the search list: reveal the app's executable in Explorer.</summary>
    private void RevealCell(HiveCell cell)
    {
        if (_state != OverlayState.TaskGrid)
            return;

        if (IsUwpCell(cell))
        {
            _overlayWindow!.ShowConfirm(Loc.Get("App_UwpNoLocation"), Loc.Get("Common_Ok"), null);
            return;
        }

        string? path = cell.ExecutablePath ?? cell.Pin?.ExecutablePath;
        if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path))
        {
            _overlayWindow!.ShowConfirm(Loc.Get("App_NoLaunchPath"), Loc.Get("Common_Ok"), null);
            return;
        }

        // Explorer takes the foreground; the overlay's Deactivated handler closes it.
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"/select,\"{path}\"",
            UseShellExecute = true
        });
    }

    /// <summary>Ctrl+S in the search list: copy exe path + original arguments to the clipboard.</summary>
    private void CopyCellCommandLine(HiveCell cell)
    {
        if (_state != OverlayState.TaskGrid)
            return;

        if (IsUwpCell(cell))
        {
            _overlayWindow!.ShowConfirm(Loc.Get("App_UwpNoLocation"), Loc.Get("Common_Ok"), null);
            return;
        }

        string? path = cell.ExecutablePath ?? cell.Pin?.ExecutablePath;
        if (string.IsNullOrEmpty(path))
        {
            _overlayWindow!.ShowConfirm(Loc.Get("App_NoLaunchPath"), Loc.Get("Common_Ok"), null);
            return;
        }

        string arguments = cell.CommandLineArguments ?? cell.Pin?.Arguments ?? string.Empty;
        string text = path.Contains(' ') ? $"\"{path}\"" : path;
        if (arguments.Length > 0)
            text += " " + arguments;

        if (TryCopyToClipboard(text))
            _overlayWindow!.ShowCopyToast();
        else
            _overlayWindow!.ShowConfirm(Loc.Get("App_CopyFailed"), Loc.Get("Common_Ok"), null);
    }

    /// <summary>SetText throws while another app holds the clipboard open; retry briefly.</summary>
    private static bool TryCopyToClipboard(string text)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Clipboard.SetText(text);
                return true;
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                System.Threading.Thread.Sleep(50);
            }
        }
        return false;
    }

    private void ActivateCell(HiveCell cell)
    {
        // A deliberate switch, not a cancel: focus goes to the chosen window, not back.
        _previousForeground = IntPtr.Zero;
        // Mark hidden up front so the Deactivated handler stays a no-op during the switch.
        _state = OverlayState.Hidden;
        if (_keyboardHook != null)
            _keyboardHook.IsOverlayOpen = false;

        if (cell.IsRunning)
        {
            WindowManager.ActivateWindow(cell.WindowHandle);
        }
        else if (cell.Pin != null)
        {
            // Relaunch the pinned program with the exact arguments it was pinned with.
            WindowManager.Launch(cell.Pin.ExecutablePath, cell.Pin.Arguments, cell.Pin.WorkingDirectory);
        }
        CloseOverlay(restoreFocus: false);
    }

    private void CloseOverlay(bool restoreFocus)
    {
        _state = OverlayState.Hidden;
        if (_keyboardHook != null)
            _keyboardHook.IsOverlayOpen = false;
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
