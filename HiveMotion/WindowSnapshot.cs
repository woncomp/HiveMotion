namespace HiveMotion;

/// <summary>An atomically published, immutable result of one background window enumeration.</summary>
public sealed class WindowSnapshot
{
    public WindowSnapshot(IReadOnlyList<RunningWindow> windows, DateTimeOffset capturedAt)
    {
        Windows = windows;
        CapturedAt = capturedAt;
    }

    public IReadOnlyList<RunningWindow> Windows { get; }
    public DateTimeOffset CapturedAt { get; }
}
