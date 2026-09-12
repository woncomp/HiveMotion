using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace HiveMotion;

internal sealed class ForegroundHandoffHost(Dispatcher dispatcher) : IForegroundHandoffHost
{
    private readonly long _origin = Stopwatch.GetTimestamp();
    public TimeSpan Now => Stopwatch.GetElapsedTime(_origin);
    public IntPtr ForegroundWindow => NativeMethods.GetForegroundWindow();
    public uint? LastInputTime
    {
        get
        {
            var info = new NativeMethods.LASTINPUTINFO
            {
                cbSize = (uint)Marshal.SizeOf<NativeMethods.LASTINPUTINFO>()
            };
            return NativeMethods.GetLastInputInfo(ref info) ? info.dwTime : null;
        }
    }

    public bool IsCurrent(WindowActivationTarget target) => WindowManager.IsCurrentTarget(target);
    public bool IsMinimized(IntPtr handle) => NativeMethods.IsIconic(handle);

    public void RequestForeground(WindowActivationTarget target, string? correlationId)
    {
        long started = Stopwatch.GetTimestamp();
        Logger.ActivationInfo($"Outgoing foreground request started; target=0x{target.Handle.ToInt64():X}.", correlationId);
        bool? restoreRequested = null;
        if (NativeMethods.IsIconic(target.Handle))
            restoreRequested = NativeMethods.ShowWindowAsync(target.Handle, NativeMethods.SW_RESTORE);
        bool foregroundRequested = NativeMethods.SetForegroundWindow(target.Handle);
        Logger.ActivationInfo($"Outgoing foreground request returned; target=0x{target.Handle.ToInt64():X}; " +
            $"restoreRequested={restoreRequested}; foregroundRequested={foregroundRequested}; " +
            $"elapsed={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1}ms.", correlationId);
    }

    public IDisposable Schedule(TimeSpan delay, Action callback) => new ScheduledCallback(dispatcher, delay, callback);

    public void Report(string outcome, WindowActivationTarget target, int attempts, TimeSpan elapsed, string? correlationId) =>
        Logger.ActivationInfo($"Foreground handoff {outcome}; target=0x{target.Handle.ToInt64():X}; " +
            $"attempts={attempts}; elapsed={elapsed.TotalMilliseconds:F1}ms.", correlationId);

    private sealed class ScheduledCallback : IDisposable
    {
        private readonly DispatcherTimer _timer;
        private Action? _callback;

        public ScheduledCallback(Dispatcher dispatcher, TimeSpan delay, Action callback)
        {
            _callback = callback;
            _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = delay };
            _timer.Tick += OnTick;
            _timer.Start();
        }

        private void OnTick(object? sender, EventArgs e)
        {
            var callback = _callback;
            Dispose();
            callback?.Invoke();
        }

        public void Dispose()
        {
            _timer.Stop();
            _timer.Tick -= OnTick;
            _callback = null;
        }
    }
}
