namespace HiveMotion;

/// <summary>Deduplicated, bounded workers. Reads and requests never execute the loader.</summary>
internal sealed class AsyncResourceCache<T> : IDisposable where T : class
{
    private sealed class Entry(string key)
    {
        public readonly string Key = key;
        public T? Value;
        public int Version;
        public bool Running;
        public bool Requested;
        public bool Visible;
        public DateTimeOffset NextCheck;
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<string, T?, T?> _load;
    private readonly Func<DateTimeOffset> _now;
    private readonly TimeSpan _retryInterval;
    private int _workers;
    private bool _disposed;
    public event Action<string>? Changed;

    public AsyncResourceCache(Func<string, T?, T?> load, Func<DateTimeOffset>? now = null,
        TimeSpan? retryInterval = null)
    {
        _load = load;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _retryInterval = retryInterval ?? TimeSpan.FromSeconds(30);
    }

    public T? Read(string key)
    {
        lock (_gate)
            return _entries.TryGetValue(key, out var entry) ? entry.Value : null;
    }

    public void Request(string key, bool visible = true)
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (!_entries.TryGetValue(key, out var entry))
                _entries.Add(key, entry = new Entry(key));
            if (!entry.Running && _now() >= entry.NextCheck)
                entry.Requested = true;
            if (!entry.Requested) return;
            entry.Visible |= visible;
            StartWorkers();
        }
    }

    public void Invalidate(string key)
    {
        lock (_gate)
        {
            if (_disposed || !_entries.TryGetValue(key, out var entry)) return;
            ++entry.Version;
            entry.NextCheck = default;
            entry.Requested = true;
            StartWorkers();
        }
    }

    // Called under _gate. A worker reserves an entry before releasing the lock.
    private void StartWorkers()
    {
        if (_workers >= 2) return;
        int available = _entries.Values.Where(e => e.Requested && !e.Running).Take(2 - _workers).Count();
        while (_workers < 2 && available-- > 0)
        {
            ++_workers;
            _ = Task.Run(Work);
        }
    }

    private void Work()
    {
        while (true)
        {
            Entry? entry;
            int version;
            T? previous;
            lock (_gate)
            {
                entry = _disposed ? null : _entries.Values
                    .Where(e => e.Requested && !e.Running).OrderByDescending(e => e.Visible).FirstOrDefault();
                if (entry == null) { --_workers; return; }
                entry.Requested = false;
                entry.Running = true;
                entry.Visible = false;
                version = entry.Version;
                previous = entry.Value;
            }

            T? result = null;
            try { result = _load(entry.Key, previous); }
            catch (Exception ex) { Logger.Error(ex, "Loading an icon resource"); }

            bool publish;
            lock (_gate)
            {
                entry.Running = false;
                publish = !_disposed && entry.Version == version;
                if (publish)
                {
                    // Transient failures retain the last usable resource, with bounded retry.
                    entry.Value = result ?? previous;
                    entry.NextCheck = _now() + _retryInterval;
                    publish = !ReferenceEquals(previous, entry.Value);
                }
            }
            if (publish)
            {
                foreach (var listener in Changed?.GetInvocationList() ?? Array.Empty<Delegate>())
                {
                    try { ((Action<string>)listener)(entry.Key); }
                    catch (Exception ex) { Logger.Error(ex, "Publishing an icon resource"); }
                }
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            Changed = null;
        }
    }
}
