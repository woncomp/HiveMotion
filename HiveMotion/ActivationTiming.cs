using System.Diagnostics;

namespace HiveMotion;

/// <summary>Monotonic activation checkpoints. "First WPF render" deliberately is not a presentation guarantee.</summary>
internal sealed class ActivationTiming
{
    private readonly long _start;

    public ActivationTiming(long? startTimestamp = null)
    {
        _start = startTimestamp ?? Stopwatch.GetTimestamp();
    }

    public void Checkpoint(string name)
    {
        // Guard before any interpolation so the disabled path allocates nothing.
        if (!Logger.IsVerboseEnabled)
            return;
        double elapsedMs = (Stopwatch.GetTimestamp() - _start) * 1000d / Stopwatch.Frequency;
        Logger.Info($"activation {name} +{elapsedMs:F1}ms thread={Environment.CurrentManagedThreadId}");
    }
}
