namespace HiveMotion;

/// <summary>
/// Opening-transition gate for snapshot-driven UI updates. While open, published snapshots
/// are not applied: only the newest is retained, and closing the gate hands it back so the
/// caller applies it exactly once, after the overlay is keyboard-ready. Members are
/// thread-safe; the retained application itself is marshalled by the caller.
/// </summary>
internal sealed class OpeningUpdateGate
{
    private readonly object _gate = new();
    private bool _open;
    private WindowSnapshot? _newest;

    public bool IsOpen
    {
        get
        {
            lock (_gate)
                return _open;
        }
    }

    public void Open()
    {
        lock (_gate)
        {
            _open = true;
            _newest = null;
        }
    }

    /// <summary>Closes the gate and returns the retained snapshot, if any; superseded ones are never surfaced.</summary>
    public WindowSnapshot? Close()
    {
        lock (_gate)
        {
            _open = false;
            WindowSnapshot? newest = _newest;
            _newest = null;
            return newest;
        }
    }

    /// <summary>While open, retains only the newest snapshot and returns true; false means the caller must apply now.</summary>
    public bool TryRetain(WindowSnapshot snapshot)
    {
        lock (_gate)
        {
            if (!_open)
                return false;
            _newest = snapshot;
            return true;
        }
    }
}
