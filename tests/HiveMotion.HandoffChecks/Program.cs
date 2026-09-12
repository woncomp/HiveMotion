using HiveMotion;

var target = new WindowActivationTarget((IntPtr)10, 100, 1000, 1);
var tests = new (string Name, Action Run)[]
{
    ("Hide immediately after the first request, without waiting for confirmation", () =>
    {
        var host = new FakeHost(target);
        var handoff = new ForegroundHandoff(host);
        handoff.Begin(target, () => host.Events.Add("hidden"));
        Check(host.Events.SequenceEqual(new[] { "request", "hidden" }), "Request/hide order is wrong.");
        Check(host.Attempts == 1 && host.Reports.Count == 0, "Begin waited or retried synchronously.");
    }),
    ("At most three attempts, followed by expiry at 300 ms", () =>
    {
        var host = new FakeHost(target);
        new ForegroundHandoff(host).Begin(target, () => { });
        host.AdvanceTo(79);
        Check(host.Attempts == 1, "Retried too soon.");
        host.AdvanceTo(80);
        host.AdvanceTo(160);
        Check(host.Attempts == 3, "Expected two additional attempts.");
        host.AdvanceTo(299);
        Check(host.Reports.Count == 0, "Stopped observing accepted requests too early.");
        host.AdvanceTo(300);
        Check(host.Attempts == 3 && host.Reports.Last() == "expired", "Failed to expire.");
    }),
    ("A dispatcher delayed by two seconds cannot issue a late retry", () =>
    {
        var host = new FakeHost(target);
        new ForegroundHandoff(host).Begin(target, () => { });
        host.AdvanceTo(2000);
        Check(host.Attempts == 1 && host.Reports.Last() == "expired", "Expired callback stole focus.");
    }),
    ("Already-foreground targets need no activation request", () =>
    {
        var host = new FakeHost(target) { ForegroundWindow = target.Handle };
        int hidden = 0;
        new ForegroundHandoff(host).Begin(target, () => hidden++);
        Check(hidden == 1 && host.Attempts == 0 && host.Reports.Last() == "confirmed", "Already active target mishandled.");
    }),
    ("Asynchronous activation can complete after the last attempt", () =>
    {
        var host = new FakeHost(target);
        var handoff = new ForegroundHandoff(host);
        handoff.Begin(target, () => { });
        host.AdvanceTo(80);
        host.AdvanceTo(160);
        host.AdvanceTo(240);
        host.ForegroundWindow = target.Handle;
        handoff.ObserveForeground();
        host.AdvanceTo(400);
        Check(host.Attempts == 3 && host.Reports.Last() == "confirmed", "Late success was missed.");
    }),
    ("Minimized target is not considered ready until restoration finishes", () =>
    {
        var host = new FakeHost(target) { ForegroundWindow = target.Handle, Minimized = true };
        var handoff = new ForegroundHandoff(host);
        handoff.Begin(target, () => { });
        Check(host.Attempts == 1 && host.Reports.Count == 0, "Minimized target was considered ready.");
        host.Minimized = false;
        handoff.ObserveForeground();
        Check(host.Reports.Last() == "confirmed", "Restored target was not confirmed.");
    }),
    ("Target disappearing before the first attempt still allows hiding", () =>
    {
        var host = new FakeHost(target) { CurrentTarget = null };
        int hidden = 0;
        new ForegroundHandoff(host).Begin(target, () => hidden++);
        Check(host.Attempts == 0 && hidden == 1 && host.Reports.Last() == "target-invalid", "Invalid target was activated.");
    }),
    ("Recycled target identity prevents retries", () =>
    {
        var host = new FakeHost(target);
        new ForegroundHandoff(host).Begin(target, () => { });
        host.CurrentTarget = target with { ProcessCreationFileTime = 2000 };
        host.AdvanceTo(80);
        Check(host.Attempts == 1 && host.Reports.Last() == "target-invalid", "Recycled identity was activated.");
    }),
    ("Reopening cancels even a callback already dequeued by the dispatcher", () =>
    {
        var host = new FakeHost(target);
        var handoff = new ForegroundHandoff(host);
        handoff.Begin(target, () => { });
        var stale = host.Scheduled.Last().Callback;
        handoff.Cancel("overlay-reopened");
        stale();
        Check(host.Attempts == 1 && host.Scheduled.All(s => s.Disposed), "Cancelled callback restarted work.");
    }),
    ("An old callback cannot cancel or retry a newer target", () =>
    {
        var host = new FakeHost(target);
        var handoff = new ForegroundHandoff(host);
        handoff.Begin(target, () => { });
        var stale = host.Scheduled.Last().Callback;
        var next = target with { Handle = (IntPtr)20 };
        host.CurrentTarget = next;
        handoff.Begin(next, () => { });
        stale();
        host.AdvanceTo(80);
        Check(host.Attempts == 3 && host.RequestedTargets.Last() == next, "Stale request interfered with its successor.");
    }),
    ("A foreground change after hiding cancels further requests", () =>
    {
        var host = new FakeHost(target);
        var handoff = new ForegroundHandoff(host);
        handoff.Begin(target, () => host.ForegroundWindow = (IntPtr)30);
        host.ForegroundWindow = (IntPtr)40;
        handoff.ObserveForeground();
        host.AdvanceTo(80);
        Check(host.Attempts == 1 && host.Reports.Last() == "foreground-changed", "Ignored a newer foreground choice.");
    }),
    ("New input to the fallback foreground also cancels retries", () =>
    {
        var host = new FakeHost(target);
        new ForegroundHandoff(host).Begin(target, () => host.ForegroundWindow = (IntPtr)30);
        host.LastInputTime++;
        host.AdvanceTo(80);
        Check(host.Attempts == 1 && host.Reports.Last() == "foreground-changed", "Retried after input to another window.");
    }),
    ("Automatic focus fallback caused by hiding does not cancel by itself", () =>
    {
        var host = new FakeHost(target);
        new ForegroundHandoff(host).Begin(target, () => host.ForegroundWindow = (IntPtr)30);
        host.AdvanceTo(80);
        Check(host.Attempts == 2, "Mistook automatic hide fallback for user intervention.");
    }),
    ("Unavailable input observation suppresses potentially intrusive retries", () =>
    {
        var host = new FakeHost(target) { LastInputTime = null };
        new ForegroundHandoff(host).Begin(target, () => { });
        host.AdvanceTo(80);
        Check(host.Attempts == 1 && host.Reports.Last() == "foreground-changed", "Retried without reliable input observation.");
    }),
    ("Cancellation during the first native request cannot schedule follow-up work", () =>
    {
        var host = new FakeHost(target);
        var handoff = new ForegroundHandoff(host);
        host.OnRequest = () => handoff.Cancel("overlay-reopened");
        handoff.Begin(target, () => { });
        host.AdvanceTo(1000);
        Check(host.Attempts == 1 && host.Scheduled.Count == 0 && host.Reports.Last() == "overlay-reopened", "Reentrant cancellation failed.");
    }),
    ("A slow first native call cannot extend the retry deadline", () =>
    {
        var host = new FakeHost(target);
        host.OnRequest = () => host.AdvanceTo(1000);
        int hidden = 0;
        new ForegroundHandoff(host).Begin(target, () => hidden++);
        Check(hidden == 1 && host.Attempts == 1 && host.Scheduled.Count == 0 && host.Reports.Last() == "expired", "Slow request extended its lifetime.");
    }),
    ("Native request failure still hides and stops retrying", () =>
    {
        var host = new FakeHost(target) { OnRequest = () => throw new InvalidOperationException() };
        int hidden = 0;
        new ForegroundHandoff(host).Begin(target, () => hidden++);
        host.AdvanceTo(1000);
        Check(hidden == 1 && host.Attempts == 1 && host.Reports.Last().StartsWith("request-failed"), "Failure left overlay open or retried.");
    })
};

int failures = 0;
foreach (var (name, run) in tests)
{
    try { run(); Console.WriteLine($"PASS {name}"); }
    catch (Exception ex) { failures++; Console.WriteLine($"FAIL {name}: {ex.Message}"); }
}
Console.WriteLine($"{tests.Length - failures}/{tests.Length} checks passed.");
return failures == 0 ? 0 : 1;

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

sealed class FakeHost(WindowActivationTarget target) : IForegroundHandoffHost
{
    public TimeSpan Now { get; private set; }
    public IntPtr ForegroundWindow { get; set; } = (IntPtr)1;
    public uint? LastInputTime { get; set; } = 1;
    public WindowActivationTarget? CurrentTarget { get; set; } = target;
    public bool Minimized { get; set; }
    public int Attempts { get; private set; }
    public Action? OnRequest { get; set; }
    public List<string> Events { get; } = new();
    public List<string> Reports { get; } = new();
    public List<WindowActivationTarget> RequestedTargets { get; } = new();
    public List<Scheduled> Scheduled { get; } = new();
    public bool IsCurrent(WindowActivationTarget candidate) => CurrentTarget == candidate;
    public bool IsMinimized(IntPtr handle) => Minimized;
    public void RequestForeground(WindowActivationTarget candidate, string? correlationId)
    {
        Attempts++;
        Events.Add("request");
        RequestedTargets.Add(candidate);
        OnRequest?.Invoke();
    }
    public IDisposable Schedule(TimeSpan delay, Action callback)
    {
        var scheduled = new Scheduled(Now + delay, callback);
        Scheduled.Add(scheduled);
        return scheduled;
    }
    public void Report(string outcome, WindowActivationTarget candidate, int attempts, TimeSpan elapsed, string? correlationId) =>
        Reports.Add(outcome);
    public void AdvanceTo(int milliseconds)
    {
        Now = TimeSpan.FromMilliseconds(milliseconds);
        foreach (var scheduled in Scheduled.Where(s => !s.Disposed && s.Due <= Now).ToArray())
        {
            scheduled.Dispose();
            scheduled.Callback();
        }
    }
}

sealed class Scheduled(TimeSpan due, Action callback) : IDisposable
{
    public TimeSpan Due { get; } = due;
    public Action Callback { get; } = callback;
    public bool Disposed { get; private set; }
    public void Dispose() => Disposed = true;
}
