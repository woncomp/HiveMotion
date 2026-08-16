using System.Diagnostics;
using System.Threading;

namespace HiveMotion;

/// <summary>Coalesces shell changes into one background scan and atomically publishes completed snapshots.</summary>
public sealed class WindowSnapshotService : IDisposable
{
    private static readonly TimeSpan ReconciliationInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DebounceInterval = TimeSpan.FromMilliseconds(75);
    private static readonly TimeSpan MaximumDebounceInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan MinimumScanInterval = TimeSpan.FromMilliseconds(250);

    private readonly WindowScanner _scanner;
    private readonly AutoResetEvent _refreshRequested = new(false);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Thread _worker;
    private readonly NativeMethods.WinEventDelegate _winEvent;
    private readonly List<IntPtr> _hooks = new();
    private readonly object _lifecycleLock = new();
    private WindowSnapshot? _latest;
    private int _disposed;
    private int _started;
    private int _foregroundRequested;

    public WindowSnapshotService(WindowScanner scanner)
    {
        _scanner = scanner;
        _winEvent = OnWinEvent;
        _worker = new Thread(WorkerLoop) { IsBackground = true, Name = "HiveMotion window snapshot" };
    }

    public event EventHandler<WindowSnapshot>? SnapshotPublished;
    /// <summary>
    /// Raised synchronously from the out-of-context WinEvent callback when an external
    /// window becomes foreground. Subscribers must return promptly and marshal UI work.
    /// </summary>
    internal event EventHandler<ForegroundWindowChangedEventArgs>? ForegroundWindowChanged;
    public WindowSnapshot? Latest => Volatile.Read(ref _latest);

    public void Start()
    {
        lock (_lifecycleLock)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (Interlocked.Exchange(ref _started, 1) != 0)
                return;

            const uint flags = NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS;
            foreach (uint eventId in new[] { NativeMethods.EVENT_OBJECT_CREATE, NativeMethods.EVENT_OBJECT_DESTROY,
                NativeMethods.EVENT_OBJECT_SHOW, NativeMethods.EVENT_OBJECT_HIDE, NativeMethods.EVENT_OBJECT_NAMECHANGE,
                NativeMethods.EVENT_SYSTEM_FOREGROUND })
            {
                IntPtr hook = NativeMethods.SetWinEventHook(eventId, eventId, IntPtr.Zero, _winEvent, 0, 0, flags);
                if (hook != IntPtr.Zero)
                    _hooks.Add(hook);
            }
            _worker.Start();
        }
        RequestRefresh();
    }

    private void OnWinEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint eventThread, uint eventTime)
    {
        if (Volatile.Read(ref _disposed) != 0 || hwnd == IntPtr.Zero)
            return;
        // Foreground is a system event; object events must name the top-level window object.
        if (eventType != NativeMethods.EVENT_SYSTEM_FOREGROUND &&
            (idObject != NativeMethods.OBJID_WINDOW || idChild != 0))
            return;
        bool foreground = eventType == NativeMethods.EVENT_SYSTEM_FOREGROUND;
        if (foreground)
            PublishForegroundWindowChanged(hwnd, eventTime);

        RequestRefresh(foreground);
    }

    private void PublishForegroundWindowChanged(IntPtr hwnd, uint eventTime)
    {
        try
        {
            ForegroundWindowChanged?.Invoke(this, new ForegroundWindowChangedEventArgs(hwnd, eventTime));
        }
        catch (Exception ex)
        {
            // A subscriber must never let an exception escape into a native WinEvent callback.
            Logger.Error(ex, "Publishing foreground-window change");
        }
    }

    public void RequestRefresh() => RequestRefresh(false);

    private void RequestRefresh(bool foreground)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;
        if (foreground)
            Volatile.Write(ref _foregroundRequested, 1);
        // This event deliberately stays allocated after shutdown: an in-flight native callback
        // can race with UnhookWinEvent, and it must never observe a disposed wait handle.
        _refreshRequested.Set();
    }

    private void WorkerLoop()
    {
        long lastScan = -Stopwatch.Frequency;
        while (!_shutdown.IsCancellationRequested)
        {
            bool requested = _refreshRequested.WaitOne(ReconciliationInterval);
            if (_shutdown.IsCancellationRequested)
                break;

            bool foreground = Interlocked.Exchange(ref _foregroundRequested, 0) != 0;
            if (requested && !foreground)
                WaitForQuietPeriod();
            if (_shutdown.IsCancellationRequested)
                break;

            long elapsed = Stopwatch.GetTimestamp() - lastScan;
            long minimum = (long)(MinimumScanInterval.TotalSeconds * Stopwatch.Frequency);
            if (elapsed < minimum && !WaitForShutdown(minimum - elapsed))
                break;

            try
            {
                var snapshot = new WindowSnapshot(_scanner.Scan(), DateTimeOffset.UtcNow);
                lastScan = Stopwatch.GetTimestamp();
                Volatile.Write(ref _latest, snapshot);
                if (Volatile.Read(ref _disposed) == 0)
                    SnapshotPublished?.Invoke(this, snapshot);
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
            }
        }
    }

    private void WaitForQuietPeriod()
    {
        long deadline = Stopwatch.GetTimestamp() +
            (long)(MaximumDebounceInterval.TotalSeconds * Stopwatch.Frequency);
        while (Stopwatch.GetTimestamp() < deadline && _refreshRequested.WaitOne(DebounceInterval))
        {
            if (_shutdown.IsCancellationRequested || Interlocked.Exchange(ref _foregroundRequested, 0) != 0)
                return;
        }
    }

    private bool WaitForShutdown(long ticks)
    {
        int milliseconds = (int)Math.Min(1000, Math.Ceiling(ticks * 1000d / Stopwatch.Frequency));
        _refreshRequested.WaitOne(milliseconds);
        return !_shutdown.IsCancellationRequested;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        lock (_lifecycleLock)
        {
            foreach (IntPtr hook in _hooks)
                NativeMethods.UnhookWinEvent(hook);
            _hooks.Clear();
            _shutdown.Cancel();
            _refreshRequested.Set();
        }
        if (_worker.IsAlive)
            _worker.Join(TimeSpan.FromSeconds(2));
        // Do not dispose _refreshRequested: callbacks already queued by Windows may still enter.
        // The cancellation source is retained too, so a bounded shutdown cannot create ODE paths.
    }
}

/// <summary>Foreground window identity reported by the system WinEvent hook.</summary>
internal sealed class ForegroundWindowChangedEventArgs(IntPtr windowHandle, uint eventTime) : EventArgs
{
    public IntPtr WindowHandle { get; } = windowHandle;
    public uint EventTime { get; } = eventTime;
}
