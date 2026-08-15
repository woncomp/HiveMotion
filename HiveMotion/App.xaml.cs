using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
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
    private WindowSnapshotService? _windowSnapshots;
    private CellAssigner? _cellAssigner;
    private PinStore? _pinStore;
    private HistoryStore? _historyStore;
    private SettingsStore? _settingsStore;
    private ManageWindow? _manageWindow;
    private LogWindow? _logWindow;
    private string _activeHotkeyJson = string.Empty;

    private OverlayState _state = OverlayState.Hidden;
    private IReadOnlyList<HiveCell> _currentCells = new List<HiveCell>();
    private IntPtr _previousForeground;
    private int _overlayGeneration;

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
        Logger.IsVerboseEnabled = _settingsStore.Settings.VerboseLogging;
        LocalizationManager.Instance.ApplyLanguageSetting(_settingsStore.Settings.Language);
        Logger.Info("Application startup completed single-instance check.");
        _windowScanner = new WindowScanner(_settingsStore.Settings.PriorityProcessNames);
        _windowSnapshots = new WindowSnapshotService(_windowScanner);
        _windowSnapshots.SnapshotPublished += OnSnapshotPublished;
        _windowSnapshots.Start();
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
        // Materialize the HWND while hidden so first activation does not pay window creation.
        _overlayWindow.PrepareHandle();
        _overlayWindow.Hide();

        _autoStartManager = new AutoStartManager();
        _trayIconManager = new TrayIconManager();
        _trayIconManager.ExitRequested += (_, _) => Shutdown();
        _trayIconManager.ManageRequested += (_, _) => Dispatcher.BeginInvoke(ShowManageWindow);
        _trayIconManager.LogRequested += (_, _) => Dispatcher.BeginInvoke(ShowLogWindow);
        _trayIconManager.ShowRequested += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            if (_state == OverlayState.Hidden)
                OpenTaskGrid();
            else
                CloseOverlay(restoreFocus: true);
        });

        _keyboardHook = BuildKeyboardHook();
        _keyboardHook.Start();
        Logger.Info("Application startup initialized tray icon and global keyboard hook.");
    }

    private GlobalKeyboardHook BuildKeyboardHook()
    {
        var settings = _settingsStore!.Settings;
        _activeHotkeyJson = System.Text.Json.JsonSerializer.Serialize(settings.Hotkeys);
        var hook = new GlobalKeyboardHook(settings.Hotkeys)
        {
            PassThroughOnSecondPress = settings.SecondPressPassthrough
        };
        hook.HotkeyOpenRequested += (_, request) =>
        {
            Logger.ActivationInfo("Queued overlay-open work on the UI dispatcher.", request.CorrelationId);
            Dispatcher.BeginInvoke(() =>
            {
                try
                {
                    Logger.ActivationInfo("Executing overlay-open work on the UI dispatcher.", request.CorrelationId);
                    OpenTaskGrid(new ActivationTiming(request.ReceiptTimestamp), request.CorrelationId, "hotkey");
                }
                catch (Exception ex)
                {
                    Logger.ActivationError(ex, "Opening overlay from hotkey", request.CorrelationId);
                }
            });
        };
        hook.HotkeyPassthrough += (_, request) =>
        {
            Logger.ActivationInfo("Queued overlay close after hotkey pass-through.", request.CorrelationId);
            Dispatcher.BeginInvoke(() =>
            {
                Logger.ActivationInfo("Executing overlay close after hotkey pass-through.", request.CorrelationId);
                // The combo went to the system (Task View & co.); the native UI takes over.
                CloseOverlay(restoreFocus: false, request.CorrelationId, LogChannel.Activation);
            });
        };
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
        _windowSnapshots?.Dispose();
        _keyboardHook?.Dispose();
        _trayIconManager?.Dispose();
        _manageWindow?.Close();
        _logWindow?.Close();
        _overlayWindow?.Close();
        if (_ownsMutex)
        {
            _singleInstanceMutex?.ReleaseMutex();
        }
        _singleInstanceMutex?.Dispose();
        Logger.Shutdown();
        base.OnExit(e);
    }

    private void OpenTaskGrid(ActivationTiming? timing = null, string? correlationId = null, string source = "tray")
    {
        timing ??= new ActivationTiming();
        LogChannel channel = source == "hotkey" ? LogChannel.Activation : LogChannel.Default;
        Logger.Info($"Overlay open requested from {source}; state={_state}.", correlationId, channel);
        timing.Checkpoint("hotkey-received-ui");
        _previousForeground = NativeMethods.GetForegroundWindow();
        Logger.Info($"Captured previous foreground handle={FormatHandle(_previousForeground)}.", correlationId, channel);

        ++_overlayGeneration;
        var snapshot = _windowSnapshots!.Latest;
        Logger.Info($"Snapshot state: {(snapshot == null ? "empty" : $"ready with {snapshot.Windows.Count} windows")}.", correlationId, channel);
        timing.Checkpoint(snapshot == null ? "snapshot-empty-shell" : "snapshot-ready");
        var cells = snapshot == null ? Array.Empty<HiveCell>() : _cellAssigner!.Assign(snapshot.Windows);
        Logger.Info($"Assigned {cells.Count} overlay cells.", correlationId, channel);
        timing.Checkpoint("cell-assignment-complete");
        _currentCells = cells;
        _state = OverlayState.TaskGrid;
        _keyboardHook!.IsOverlayOpen = true;
        Logger.Info("Updated overlay and keyboard-hook state to open.", correlationId, channel);
        _overlayWindow!.ShowTaskGrid(cells, timing, correlationId, channel);
        if (snapshot != null)
            RecordHistoryWhenIdle(snapshot.Windows);
        _windowSnapshots.RequestRefresh();
    }

    /// <summary>Re-scans and rebuilds the grid in place after a pin/unpin, keeping the overlay open.</summary>
    private void RefreshTaskGrid()
    {
        try
        {
            var snapshot = _windowSnapshots!.Latest;
            if (snapshot == null)
            {
                _windowSnapshots.RequestRefresh();
                return;
            }
            var cells = _cellAssigner!.Assign(snapshot.Windows);
            _currentCells = cells;
            _overlayWindow!.UpdateCells(cells);
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
        }
    }

    private void OnSnapshotPublished(object? sender, WindowSnapshot snapshot)
    {
        // The worker never touches WPF or history state. A newer snapshot may replace an empty/older grid.
        Dispatcher.BeginInvoke(() =>
        {
            if (_state != OverlayState.TaskGrid)
                return;
            int generation = _overlayGeneration;
            var cells = _cellAssigner!.Assign(snapshot.Windows);
            if (_state == OverlayState.TaskGrid && generation == _overlayGeneration)
            {
                _currentCells = cells;
                _overlayWindow!.UpdateCells(cells);
            }
        });
    }

    /// <summary>History owns UI-thread state; defer synchronous persistence beyond interaction work.</summary>
    private void RecordHistoryWhenIdle(IReadOnlyList<RunningWindow> windows)
    {
        Dispatcher.BeginInvoke(() =>
        {
            try
            {
                _historyStore!.RecordScan(windows);
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
            }
        }, DispatcherPriority.ApplicationIdle);
    }

    /// <summary>Manage center is a singleton normal window; reopening just brings it forward.</summary>
    private void ShowManageWindow()
    {
        if (_manageWindow == null)
        {
            // Manage's occasional UI-thread scans use their own cache; the snapshot scanner is single-worker.
            _manageWindow = new ManageWindow(_pinStore!, _historyStore!, _settingsStore!,
                _autoStartManager!, new WindowScanner(_settingsStore!.Settings.PriorityProcessNames), ApplyHotkeySettings);
            _manageWindow.Closed += (_, _) => _manageWindow = null;
            _manageWindow.Show();
        }
        else if (_manageWindow.WindowState == WindowState.Minimized)
        {
            _manageWindow.WindowState = WindowState.Normal;
        }

        // Plain Activate() is denied for a background process; use the attach-input recipe.
        WindowManager.ActivateWindow(new System.Windows.Interop.WindowInteropHelper(_manageWindow).Handle);
    }

    /// <summary>Log viewer is a singleton normal window; reopening restores and focuses it.</summary>
    private void ShowLogWindow()
    {
        if (_logWindow == null)
        {
            _logWindow = new LogWindow();
            _logWindow.Closed += (_, _) => _logWindow = null;
            _logWindow.Show();
            Logger.Info("Opened live log window.");
        }
        else
        {
            if (_logWindow.WindowState == WindowState.Minimized)
                _logWindow.WindowState = WindowState.Normal;
            _logWindow.Activate();
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

        if (!IsCurrentWindowInstance(cell))
        {
            _windowSnapshots!.RequestRefresh();
            return;
        }

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
            if (IsCurrentWindowInstance(cell))
            {
                WindowManager.ActivateWindow(cell.WindowHandle);
            }
            else
            {
                // Do not activate an HWND recycled for another process. Keep the launcher usable and refresh it.
                _state = OverlayState.TaskGrid;
                _keyboardHook!.IsOverlayOpen = true;
                _windowSnapshots!.RequestRefresh();
                return;
            }
        }
        else if (cell.Pin != null)
        {
            // Relaunch the pinned program with the exact arguments it was pinned with.
            WindowManager.Launch(cell.Pin.ExecutablePath, cell.Pin.Arguments, cell.Pin.WorkingDirectory);
        }
        CloseOverlay(restoreFocus: false);
    }

    private void CloseOverlay(bool restoreFocus, string? correlationId = null, LogChannel channel = LogChannel.Default)
    {
        Logger.Info($"Closing overlay; restoreFocus={restoreFocus}; state={_state}.", correlationId, channel);
        ++_overlayGeneration;
        _state = OverlayState.Hidden;
        if (_keyboardHook != null)
            _keyboardHook.IsOverlayOpen = false;
        _overlayWindow!.HideOverlay();
        _currentCells = new List<HiveCell>();

        if (restoreFocus && _previousForeground != IntPtr.Zero)
        {
            var target = _previousForeground;
            _previousForeground = IntPtr.Zero;
            Dispatcher.BeginInvoke(() =>
            {
                Logger.Info($"Restoring foreground handle={FormatHandle(target)}.", correlationId, LogChannel.Activation);
                WindowManager.ActivateWindow(target);
            });
        }
        else
        {
            _previousForeground = IntPtr.Zero;
        }
    }

    private static string FormatHandle(IntPtr handle) => $"0x{handle.ToInt64():X}";

    private static bool IsCurrentWindowInstance(HiveCell cell)
    {
        if (cell.ProcessCreationFileTime == 0 || !NativeMethods.IsWindow(cell.WindowHandle))
            return false;
        NativeMethods.GetWindowThreadProcessId(cell.WindowHandle, out uint pid);
        if (pid != cell.ProcessId)
            return false;
        IntPtr process = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == IntPtr.Zero)
            return false;
        try
        {
            return NativeMethods.GetProcessTimes(process, out long created, out _, out _, out _) &&
                   created == cell.ProcessCreationFileTime;
        }
        finally { NativeMethods.CloseHandle(process); }
    }
}
