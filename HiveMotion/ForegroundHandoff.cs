using System;

namespace HiveMotion;

internal readonly record struct WindowActivationTarget(IntPtr Handle, uint ProcessId,
    long ProcessCreationFileTime, uint ThreadId);

/// <summary>Native operations and scheduling are separate so handoff races can be tested without a desktop.</summary>
internal interface IForegroundHandoffHost
{
    TimeSpan Now { get; }
    IntPtr ForegroundWindow { get; }
    uint? LastInputTime { get; }
    bool IsCurrent(WindowActivationTarget target);
    bool IsMinimized(IntPtr handle);
    void RequestForeground(WindowActivationTarget target, string? correlationId);
    IDisposable Schedule(TimeSpan delay, Action callback);
    void Report(string outcome, WindowActivationTarget target, int attempts, TimeSpan elapsed, string? correlationId);
}

/// <summary>
/// A dispatcher-owned, latest-request-only handoff. The deadline bounds future requests,
/// not native API execution or work Windows has already accepted.
/// </summary>
internal sealed class ForegroundHandoff(IForegroundHandoffHost host)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromMilliseconds(80);
    private const int MaximumAttempts = 3;
    private Request? _pending;

    private sealed class Request(WindowActivationTarget target, TimeSpan startedAt, string? correlationId)
    {
        public WindowActivationTarget Target { get; } = target;
        public TimeSpan StartedAt { get; } = startedAt;
        public string? CorrelationId { get; } = correlationId;
        public int Attempts { get; set; }
        public bool Hidden { get; set; }
        public IntPtr ForegroundAfterHide { get; set; }
        public uint? InputAfterHide { get; set; }
        public IDisposable? Scheduled { get; set; }
    }

    public void Begin(WindowActivationTarget target, Action hideOverlay, string? correlationId = null)
    {
        Cancel("superseded");
        var request = new Request(target, host.Now, correlationId);
        _pending = request;
        try
        {
            // Use the current foreground privilege, but never wait for confirmation before hiding.
            Advance(request, attempt: true);
        }
        finally
        {
            hideOverlay();
        }

        if (_pending != request)
            return;
        request.Hidden = true;
        request.ForegroundAfterHide = host.ForegroundWindow;
        request.InputAfterHide = host.LastInputTime;
        Advance(request, attempt: false);
        ScheduleNext(request);
    }

    public void Cancel(string reason = "cancelled")
    {
        if (_pending is { } request)
            Finish(request, reason);
    }

    public void ObserveForeground()
    {
        if (_pending is { Hidden: true } request)
            Advance(request, attempt: false);
    }

    private void Advance(Request request, bool attempt)
    {
        if (_pending != request)
            return;
        if (!host.IsCurrent(request.Target))
        {
            Finish(request, "target-invalid");
            return;
        }

        IntPtr foreground = host.ForegroundWindow;
        if (foreground == request.Target.Handle && !host.IsMinimized(request.Target.Handle))
        {
            Finish(request, "confirmed");
            return;
        }
        if (host.Now - request.StartedAt >= Lifetime)
        {
            Finish(request, "expired");
            return;
        }

        if (request.Hidden && foreground != IntPtr.Zero && foreground != request.Target.Handle)
        {
            uint? input = host.LastInputTime;
            // Hiding can itself select another foreground window. Accept that initial fallback,
            // but yield on subsequent foreground changes or any new input delivered elsewhere.
            // Input observation failure also suppresses retries rather than stealing focus.
            if (foreground != request.ForegroundAfterHide || input == null ||
                request.InputAfterHide == null || input != request.InputAfterHide)
            {
                Finish(request, "foreground-changed");
                return;
            }
        }

        if (!attempt || request.Attempts >= MaximumAttempts)
            return;
        ++request.Attempts;
        try
        {
            host.RequestForeground(request.Target, request.CorrelationId);
        }
        catch (Exception ex)
        {
            Finish(request, $"request-failed ({ex.GetType().Name})");
        }
        // A successful native request may still need the target's message loop to activate it.
        // Recheck on an event or a later tick, without attaching input queues or sleeping.
    }

    private void ScheduleNext(Request request)
    {
        if (_pending != request)
            return;
        TimeSpan remaining = Lifetime - (host.Now - request.StartedAt);
        if (remaining <= TimeSpan.Zero)
        {
            Finish(request, "expired");
            return;
        }
        TimeSpan delay = request.Attempts < MaximumAttempts && RetryInterval < remaining
            ? RetryInterval : remaining;
        request.Scheduled = host.Schedule(delay, () =>
        {
            if (_pending != request)
                return;
            request.Scheduled = null;
            Advance(request, attempt: true);
            ScheduleNext(request);
        });
    }

    private void Finish(Request request, string outcome)
    {
        if (_pending != request)
            return;
        _pending = null;
        request.Scheduled?.Dispose();
        host.Report(outcome, request.Target, request.Attempts, host.Now - request.StartedAt, request.CorrelationId);
    }
}
