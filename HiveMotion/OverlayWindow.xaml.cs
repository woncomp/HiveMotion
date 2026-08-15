using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace HiveMotion;

public partial class OverlayWindow : Window
{
    private DispatcherTimer? _activationRetryTimer;
    private int _activationGeneration;
    private int _activationRetryCount;
    private bool _firstWpfRenderForActivation;
    private ActivationTiming? _activationTiming;
    private EventHandler? _firstRenderHandler;

    private const int BackdropCacheCapacity = 4;
    private static readonly TimeSpan BackdropCacheMaxAge = TimeSpan.FromSeconds(15);
    private static readonly object BackdropCacheLock = new();
    private static readonly Dictionary<MonitorIdentity, CachedBackdrop> BackdropCache = new();
    private static readonly HashSet<MonitorIdentity> BackdropCapturesInFlight = new();

    private readonly record struct MonitorIdentity(string DeviceName, int Left, int Top, int Width, int Height);
    private readonly record struct CachedBackdrop(ImageSource Image, long CapturedAt);
    public OverlayWindow()
    {
        InitializeComponent();

        // Grid hotkeys must reach WPF unprocessed by any IME; the search box
        // re-enables IME locally on SearchInput (the property is inherited).
        InputMethod.SetIsInputMethodEnabled(this, false);

        TaskGrid.CellChosen += (_, cell) => CellChosen?.Invoke(this, cell);
        TaskGrid.CloseRequested += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);
        TaskGrid.BackRequested += (_, _) => BackRequested?.Invoke(this, EventArgs.Empty);
        TaskGrid.PinToggleRequested += (_, cell) => PinToggleRequested?.Invoke(this, cell);
        TaskGrid.RevealRequested += (_, cell) => RevealRequested?.Invoke(this, cell);
        TaskGrid.CopyCommandRequested += (_, cell) => CopyCommandRequested?.Invoke(this, cell);
        KeyDown += (_, e) => TaskGrid.HandleWindowKeyDown(e);
    }

    public event EventHandler<HiveCell>? CellChosen;
    public event EventHandler? CloseRequested;
    /// <summary>Backspace on the grid: pop one layer (folder → home).</summary>
    public event EventHandler? BackRequested;
    public event EventHandler<HiveCell>? PinToggleRequested;
    public event EventHandler<HiveCell>? RevealRequested;
    public event EventHandler<HiveCell>? CopyCommandRequested;
    public event EventHandler? FirstWpfRender;

    /// <summary>Rebuilds the cells in place after a pin change, without re-showing the overlay.</summary>
    public void UpdateCells(IReadOnlyList<HiveCell> cells) =>
        Dispatcher.BeginInvoke(() => TaskGrid.SetCells(cells));

    /// <summary>Switches the grid chrome (Esc hint) between the home layer and a folder layer.</summary>
    public void SetActiveFolder(string? folderName) =>
        Dispatcher.BeginInvoke(() => TaskGrid.SetActiveFolder(folderName));

    /// <summary>Modal in-overlay question; null action shows a dismiss-only notice.</summary>
    public void ShowConfirm(string message, string confirmText, Action? onConfirm) =>
        Dispatcher.BeginInvoke(() => TaskGrid.ShowConfirm(message, confirmText, onConfirm));

    /// <summary>Brief "copied" pill; the overlay stays open.</summary>
    public void ShowCopyToast() =>
        Dispatcher.BeginInvoke(() => TaskGrid.ShowCopyToast());

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var helper = new WindowInteropHelper(this);

        // Hide from Alt+Tab but take real keyboard focus when shown (IME + default editing work).
        int exStyle = NativeMethods.GetWindowLong(helper.Handle, NativeMethods.GWL_EXSTYLE);
        exStyle = (exStyle | NativeMethods.WS_EX_TOOLWINDOW) & ~NativeMethods.WS_EX_APPWINDOW;
        NativeMethods.SetWindowLong(helper.Handle, NativeMethods.GWL_EXSTYLE, exStyle);

        TaskGrid.OverlayHwnd = helper.Handle;
    }

    /// <summary>Creates the WPF HWND at startup without showing or activating the overlay.</summary>
    public void PrepareHandle()
    {
        _ = new WindowInteropHelper(this).EnsureHandle();
        Dispatcher.BeginInvoke(new Action(WarmBackdropForCursorMonitor), DispatcherPriority.ApplicationIdle);
    }

    internal void ShowTaskGrid(IReadOnlyList<HiveCell> cells, ActivationTiming? timing = null,
        string? correlationId = null, LogChannel channel = LogChannel.Default)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => ShowTaskGrid(cells, timing, correlationId, channel));
            return;
        }
        Logger.Info($"Queued overlay visual presentation for {cells.Count} cells.", correlationId, channel);
        _activationTiming = timing;
        _activationTiming?.Checkpoint("overlay-ui-start");
        int generation = ++_activationGeneration;
        _firstWpfRenderForActivation = false;
        DisarmFirstRenderNotification();
        CancelActivationRetries();
        MoveToCursorScreen(correlationId, channel);
        Logger.Info($"Selected overlay screen; {DescribeGeometry()}.", correlationId, channel);
        if (TryGetCachedBackdrop(_screen, out var backdrop))
        {
            _activationTiming?.Checkpoint("backdrop-cache-hit");
            Logger.Info("Using cached blurred backdrop.", correlationId, channel);
            TaskGrid.SetBackdrop(backdrop);
        }
        else
        {
            // Do not capture while activating: the dim layer is the safe fallback until a
            // hidden-period cache warm succeeds.
            _activationTiming?.Checkpoint("backdrop-cache-miss");
            Logger.Info("No cached blurred backdrop available.", correlationId, channel);
            TaskGrid.SetBackdrop(null);
        }
        TaskGrid.SetCells(cells);
        Logger.Info($"Updated overlay grid with {cells.Count} cells.", correlationId, channel);
        // Hide the cursor and suspend hover/clicks until the user actually moves the mouse.
        TaskGrid.DisarmMouse();
        ArmFirstRenderNotification(generation);
        Show();
        _activationTiming?.Checkpoint("overlay-shown");
        Logger.Info("Overlay window Show completed.", correlationId, channel);
        bool focused = Focus();
        Logger.Info($"Overlay focus requested; result={focused}.", correlationId, channel);
        // Re-assert the top of the topmost band: when activation is denied, another
        // always-on-top window would otherwise cover the grid.
        NativeMethods.SetWindowPos(TaskGrid.OverlayHwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
        // One non-sleeping foreground attempt is allowed on the activation path.
        WindowManager.ActivateWindowOnce(TaskGrid.OverlayHwnd, correlationId);
        // Windows may apply its own DPI-suggested rect on the cross-DPI hop; re-assert ours.
        Dispatcher.BeginInvoke(() =>
        {
            ApplyScreenBounds();
            Logger.Info($"Re-applied screen bounds; {DescribeGeometry()}.", correlationId, channel);
        });
        ScheduleActivationRetries(generation);
    }

    private void ScheduleActivationRetries(int generation)
    {
        _activationRetryCount = 0;
        _activationRetryTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(80)
        };
        _activationRetryTimer.Tick += (_, _) =>
        {
            if (generation != _activationGeneration || !IsVisible)
            {
                CancelActivationRetries();
                return;
            }
            // Retries begin only after this activation has rendered a frame.
            if (!_firstWpfRenderForActivation)
                return;
            if (++_activationRetryCount > 2)
            {
                CancelActivationRetries();
                return;
            }
            if (NativeMethods.GetForegroundWindow() == TaskGrid.OverlayHwnd)
            {
                CancelActivationRetries();
                return;
            }
            // Exactly one non-sleeping foreground attempt per dispatcher tick.
            WindowManager.ActivateWindowOnce(TaskGrid.OverlayHwnd);
            NativeMethods.SetWindowPos(TaskGrid.OverlayHwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
        };
        _activationRetryTimer.Start();
    }

    private void CancelActivationRetries()
    {
        _activationRetryTimer?.Stop();
        _activationRetryTimer = null;
    }

    private void ArmFirstRenderNotification(int generation)
    {
        _firstRenderHandler = (_, _) =>
        {
            if (generation != _activationGeneration || !IsVisible)
                return;

            DisarmFirstRenderNotification();
            _firstWpfRenderForActivation = true;
            _activationTiming?.Checkpoint("first-wpf-render (not hardware presentation)");
            _activationTiming = null;
            FirstWpfRender?.Invoke(this, EventArgs.Empty);
        };
        CompositionTarget.Rendering += _firstRenderHandler;
    }

    private void DisarmFirstRenderNotification()
    {
        if (_firstRenderHandler == null)
            return;
        CompositionTarget.Rendering -= _firstRenderHandler;
        _firstRenderHandler = null;
    }

    protected override void OnDeactivated(EventArgs e)
    {
        CancelActivationRetries();
        base.OnDeactivated(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        DisarmFirstRenderNotification();
        CancelActivationRetries();
        base.OnClosed(e);
    }

    private System.Windows.Forms.Screen _screen = System.Windows.Forms.Screen.PrimaryScreen!;

    public string DescribeGeometry() =>
        $"screen={_screen.DeviceName} bounds={_screen.Bounds} window=({Left},{Top},{Width},{Height})";

    /// <summary>
    /// Cover the screen that currently holds the mouse cursor.
    /// Position is set with SetWindowPos in PHYSICAL pixels: assigning WPF Left/Width in DIPs
    /// converts through the window's CURRENT monitor DPI, which lands the window in empty
    /// space when moving between monitors with different scaling.
    /// </summary>
    private void MoveToCursorScreen(string? correlationId = null, LogChannel channel = LogChannel.Default)
    {
        try
        {
            if (!NativeMethods.GetCursorPos(out var point))
            {
                Logger.Warning("Unable to read cursor position; retaining the existing screen.", correlationId, channel);
                return;
            }

            _screen = System.Windows.Forms.Screen.FromPoint(
                new System.Drawing.Point(point.x, point.y));
            ApplyScreenBounds();
            Logger.Info($"Moved overlay to cursor screen {_screen.DeviceName}; cursor=({point.x},{point.y}).", correlationId, channel);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Selecting the cursor screen for the overlay", correlationId, channel);
            // fall back to wherever the window already is
        }
    }

    private void ApplyScreenBounds()
    {
        // EnsureHandle lets the first open position the window before its first Show.
        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        var b = _screen.Bounds;
        NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, b.Left, b.Top, b.Width, b.Height,
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
    }

    /// <summary>
    /// Snapshot one display directly to a low-resolution bitmap. Its device DC preserves
    /// PerMonitorV2 physical bounds, including negative and stacked monitor layouts.
    /// </summary>
    private static System.Windows.Media.ImageSource? CaptureBlurredBackdrop(System.Windows.Forms.Screen screen,
        string? correlationId = null, LogChannel channel = LogChannel.Default)
    {
        int width = screen.Bounds.Width;
        int height = screen.Bounds.Height;

        IntPtr hdcScreen = IntPtr.Zero;
        IntPtr hdcMem = IntPtr.Zero;
        IntPtr hBitmap = IntPtr.Zero;
        IntPtr hOld = IntPtr.Zero;
        try
        {
            hdcScreen = NativeMethods.CreateDC(screen.DeviceName, null, null, IntPtr.Zero);
            if (hdcScreen == IntPtr.Zero)
                return null;
            hdcMem = NativeMethods.CreateCompatibleDC(hdcScreen);
            if (hdcMem == IntPtr.Zero)
                return null;
            int smallWidth = Math.Max(1, width / 4);
            int smallHeight = Math.Max(1, height / 4);
            hBitmap = NativeMethods.CreateCompatibleBitmap(hdcScreen, smallWidth, smallHeight);
            if (hBitmap == IntPtr.Zero)
                return null;
            hOld = NativeMethods.SelectObject(hdcMem, hBitmap);
            if (hOld == IntPtr.Zero || hOld == NativeMethods.HGDI_ERROR)
                return null;
            Logger.Info($"Capturing blurred backdrop; screen={screen.DeviceName} ({width}x{height}).", correlationId, channel);
            NativeMethods.SetStretchBltMode(hdcMem, NativeMethods.HALFTONE);
            if (!NativeMethods.StretchBlt(hdcMem, 0, 0, smallWidth, smallHeight, hdcScreen, 0, 0, width, height, NativeMethods.SRCCOPY))
            {
                Logger.Warning("StretchBlt failed while capturing blurred backdrop.", correlationId, channel);
                return null;
            }
            var source = Imaging.CreateBitmapSourceFromHBitmap(
                hBitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            Logger.Info("Blurred-backdrop capture completed.", correlationId, channel);
            return source;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Capturing the blurred overlay backdrop", correlationId, channel);
            return null;
        }
        finally
        {
            if (hOld != IntPtr.Zero && hOld != NativeMethods.HGDI_ERROR && hdcMem != IntPtr.Zero)
                NativeMethods.SelectObject(hdcMem, hOld);
            if (hBitmap != IntPtr.Zero)
                NativeMethods.DeleteObject(hBitmap);
            if (hdcMem != IntPtr.Zero)
                NativeMethods.DeleteDC(hdcMem);
            if (hdcScreen != IntPtr.Zero)
                NativeMethods.DeleteDC(hdcScreen);
        }
    }

    private static MonitorIdentity GetMonitorIdentity(System.Windows.Forms.Screen screen)
    {
        var bounds = screen.Bounds;
        return new MonitorIdentity(screen.DeviceName, bounds.Left, bounds.Top, bounds.Width, bounds.Height);
    }

    private static bool TryGetCachedBackdrop(System.Windows.Forms.Screen screen, out ImageSource? backdrop)
    {
        var identity = GetMonitorIdentity(screen);
        long now = Stopwatch.GetTimestamp();
        lock (BackdropCacheLock)
        {
            if (BackdropCache.TryGetValue(identity, out var cached) &&
                Stopwatch.GetElapsedTime(cached.CapturedAt, now) <= BackdropCacheMaxAge)
            {
                backdrop = cached.Image;
                return true;
            }
            BackdropCache.Remove(identity);
        }
        backdrop = null;
        return false;
    }

    /// <summary>Starts one best-effort GDI capture only while the overlay is hidden.</summary>
    private void WarmBackdropForCursorMonitor()
    {
        if (IsVisible || !NativeMethods.GetCursorPos(out var point))
            return;

        var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(point.x, point.y));
        var identity = GetMonitorIdentity(screen);
        int captureGeneration = Volatile.Read(ref _activationGeneration);
        IntPtr overlayHwnd = TaskGrid.OverlayHwnd;
        if (TryGetCachedBackdrop(screen, out _))
            return;

        lock (BackdropCacheLock)
        {
            if (!BackdropCapturesInFlight.Add(identity))
                return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                // This work is only queued after checking hidden state; it is never scheduled
                // from ShowTaskGrid and its frozen result is safe to publish across threads.
                if (NativeMethods.IsWindowVisible(overlayHwnd))
                    return;
                var image = CaptureBlurredBackdrop(screen);
                // Do not publish a frame if an activation began during capture. This prevents
                // a capture racing Show from ever becoming the overlay's own backdrop.
                if (image == null || captureGeneration != Volatile.Read(ref _activationGeneration) ||
                    NativeMethods.IsWindowVisible(overlayHwnd))
                    return;
                lock (BackdropCacheLock)
                {
                    while (BackdropCache.Count >= BackdropCacheCapacity)
                    {
                        MonitorIdentity oldest = default;
                        long oldestTime = long.MaxValue;
                        foreach (var entry in BackdropCache)
                        {
                            if (entry.Value.CapturedAt < oldestTime)
                            {
                                oldest = entry.Key;
                                oldestTime = entry.Value.CapturedAt;
                            }
                        }
                        BackdropCache.Remove(oldest);
                    }
                    BackdropCache[identity] = new CachedBackdrop(image, Stopwatch.GetTimestamp());
                }
            }
            finally
            {
                lock (BackdropCacheLock)
                    BackdropCapturesInFlight.Remove(identity);
            }
        });
    }

    public void HideOverlay()
    {
        Dispatcher.BeginInvoke(() =>
        {
            ++_activationGeneration;
            DisarmFirstRenderNotification();
            CancelActivationRetries();
            TaskGrid.ResetPreview();
            TaskGrid.ArmMouse();
            Hide();
            Dispatcher.BeginInvoke(new Action(WarmBackdropForCursorMonitor), DispatcherPriority.ApplicationIdle);
        });
    }
}
