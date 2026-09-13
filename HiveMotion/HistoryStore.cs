using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace HiveMotion;

/// <summary>
/// Persists identities consumed when the hive opens to history.json. Each consumed snapshot feeds in:
/// an identity absent from the previous scan counts as a fresh launch (LaunchCount++),
/// everything refreshes LastSeen. Identities with unreadable arguments are kept as
/// exe-only entries, matching the exe-only pin fallback.
///
/// Persistence is asynchronous with coalescing: mutating calls snapshot the entries on
/// the calling (UI) thread and hand the snapshot to a single background writer, so JSON
/// serialization and disk I/O never run on the UI hot path. Repeated mutations while a
/// write is in flight collapse into one follow-up write of the newest snapshot.
/// </summary>
public sealed class HistoryStore
{
    private const int MaxEntries = 200;

    private static readonly string DefaultStoreFile =
        Path.Combine(MotionStore.StoreDirectoryPath, "history.json");

    public List<HistoryEntry> Entries { get; } = new();

    /// <summary>Identity keys of the previous scan; the diff between scans is what counts as a launch.</summary>
    private HashSet<string> _previousKeys = new(StringComparer.Ordinal);

    private readonly string _storeFile;
    private readonly TimeSpan _writeDelay;

    private readonly object _saveGate = new();
    private List<HistoryEntry>? _pendingSnapshot;
    private bool _writerRunning;

    private readonly object _existenceGate = new();
    private readonly Dictionary<string, bool> _fileExists = new(StringComparer.OrdinalIgnoreCase);
    private bool _existenceRefreshing;

    public HistoryStore() : this(null) { }

    internal HistoryStore(string? storeFilePath, TimeSpan? writeDelay = null)
    {
        _storeFile = storeFilePath ?? DefaultStoreFile;
        _writeDelay = writeDelay ?? TimeSpan.Zero;
        Load();
    }

    /// <summary>Completed background writes; test-only visibility for coalescing checks.</summary>
    internal int WritesCompleted { get; private set; }

    public void RecordScan(IReadOnlyList<RunningWindow> windows)
    {
        var now = DateTime.Now;
        var currentKeys = new HashSet<string>(StringComparer.Ordinal);
        bool dirty = false;

        foreach (var window in windows)
        {
            // UWP identities cannot be relaunched; without an exe path there is no identity at all.
            if (window.ExecutablePath == null ||
                window.ProcessName.Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase))
                continue;

            string arguments = window.CommandLineArguments ?? string.Empty;
            string key = HistoryEntry.Key(window.ExecutablePath, arguments);
            if (!currentKeys.Add(key))
                continue; // several windows of one process share the identity

            var entry = Entries.FirstOrDefault(e => e.IdentityKey == key);
            if (entry == null)
            {
                Entries.Add(new HistoryEntry
                {
                    ProcessName = window.ProcessName,
                    ExecutablePath = window.ExecutablePath,
                    Arguments = arguments,
                    WorkingDirectory = window.WorkingDirectory ?? string.Empty,
                    DisplayName = window.AppName,
                    LaunchCount = 1,
                    FirstSeen = now,
                    LastSeen = now
                });
                dirty = true;
            }
            else
            {
                if (!_previousKeys.Contains(key))
                    entry.LaunchCount++;
                entry.LastSeen = now;
                entry.DisplayName = window.AppName;
                entry.ProcessName = window.ProcessName;
                dirty = true;
            }
        }

        _previousKeys = currentKeys;

        if (Entries.Count > MaxEntries)
        {
            // Evict the least-recently-launched identities first.
            Entries.Sort((a, b) => b.LastSeen.CompareTo(a.LastSeen));
            Entries.RemoveRange(MaxEntries, Entries.Count - MaxEntries);
            dirty = true;
        }

        if (dirty)
            Save();
    }

    /// <summary>
    /// Picker order: existing files first, then most-launched, then most-recent. Existence
    /// comes from a cache refreshed on a background thread (paths are collected here, only
    /// File.Exists runs off-thread); unknown paths are treated as existing so the cold cache
    /// degrades to launch-count ordering instead of inverting it.
    /// </summary>
    public IReadOnlyList<HistoryEntry> SortedForPicker()
    {
        QueueExistenceRefresh(Entries.Select(e => e.ExecutablePath).ToArray());
        return Entries
            .OrderByDescending(e => LookupExists(e.ExecutablePath))
            .ThenByDescending(e => e.LaunchCount)
            .ThenByDescending(e => e.LastSeen)
            .ToList();
    }

    public void Clear()
    {
        Entries.Clear();
        _previousKeys.Clear();
        Save();
    }

    /// <summary>Import path: replaces every entry in one shot.</summary>
    public void ReplaceAll(IEnumerable<HistoryEntry> entries)
    {
        Entries.Clear();
        Entries.AddRange(entries);
        Save();
    }

    /// <summary>Waits (bounded) until the background writer has drained; call at shutdown.</summary>
    public void Flush()
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < TimeSpan.FromSeconds(2))
        {
            lock (_saveGate)
            {
                if (!_writerRunning && _pendingSnapshot == null)
                    return;
            }
            System.Threading.Thread.Sleep(10);
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_storeFile))
                return;
            var entries = JsonSerializer.Deserialize<List<HistoryEntry>>(File.ReadAllText(_storeFile));
            if (entries == null)
                return;
            Entries.Clear();
            Entries.AddRange(entries.Where(e => !string.IsNullOrEmpty(e.ExecutablePath)));
        }
        catch
        {
            // A corrupt store must never block startup; begin with no history.
        }
    }

    /// <summary>Snapshot on the calling thread, serialize and write on a single background writer.</summary>
    private void Save()
    {
        lock (_saveGate)
        {
            _pendingSnapshot = TakeSnapshot();
            if (_writerRunning)
                return;
            _writerRunning = true;
        }
        _ = System.Threading.Tasks.Task.Run(WriteLoop);
    }

    private void WriteLoop()
    {
        try
        {
            while (true)
            {
                List<HistoryEntry> snapshot;
                lock (_saveGate)
                {
                    snapshot = _pendingSnapshot!;
                    _pendingSnapshot = null;
                }
                if (_writeDelay > TimeSpan.Zero)
                    System.Threading.Thread.Sleep(_writeDelay);
                WriteSnapshot(snapshot);
                lock (_saveGate)
                {
                    if (_pendingSnapshot == null)
                        return;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "History background writer failed");
        }
        finally
        {
            lock (_saveGate)
                _writerRunning = false;
        }
    }

    /// <summary>Deep copy so the writer never observes entries mutated after Save() returns.</summary>
    private List<HistoryEntry> TakeSnapshot() =>
        Entries.Select(e => new HistoryEntry
        {
            ProcessName = e.ProcessName,
            ExecutablePath = e.ExecutablePath,
            Arguments = e.Arguments,
            WorkingDirectory = e.WorkingDirectory,
            DisplayName = e.DisplayName,
            LaunchCount = e.LaunchCount,
            FirstSeen = e.FirstSeen,
            LastSeen = e.LastSeen
        }).ToList();

    private void WriteSnapshot(List<HistoryEntry> snapshot)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_storeFile)!);
            File.WriteAllText(_storeFile,
                JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }));
            WritesCompleted++;
        }
        catch
        {
            // best effort; history stays in memory for this session
        }
    }

    private bool LookupExists(string path)
    {
        lock (_existenceGate)
            return !_fileExists.TryGetValue(path, out bool exists) || exists;
    }

    /// <summary>Runs File.Exists off the UI thread; the picker keeps serving the cached flags meanwhile.</summary>
    private void QueueExistenceRefresh(string[] paths)
    {
        lock (_existenceGate)
        {
            if (_existenceRefreshing)
                return;
            _existenceRefreshing = true;
        }
        _ = System.Threading.Tasks.Task.Run(() =>
        {
            var results = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    results[path] = File.Exists(path);
                }
                catch
                {
                    // Unreachable path (too long, invalid chars): keep the optimistic cached default.
                }
            }
            lock (_existenceGate)
            {
                foreach (var pair in results)
                    _fileExists[pair.Key] = pair.Value;
                _existenceRefreshing = false;
            }
        });
    }
}
