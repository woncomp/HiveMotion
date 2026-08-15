using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace HiveMotion;

/// <summary>
/// Persists identities consumed when the hive opens to history.json. Each consumed snapshot feeds in:
/// an identity absent from the previous scan counts as a fresh launch (LaunchCount++),
/// everything refreshes LastSeen. Identities with unreadable arguments are kept as
/// exe-only entries, matching the exe-only pin fallback.
/// </summary>
public sealed class HistoryStore
{
    private const int MaxEntries = 200;

    private static readonly string StoreFile =
        Path.Combine(MotionStore.StoreDirectoryPath, "history.json");

    public List<HistoryEntry> Entries { get; } = new();

    /// <summary>Identity keys of the previous scan; the diff between scans is what counts as a launch.</summary>
    private HashSet<string> _previousKeys = new(StringComparer.Ordinal);

    public HistoryStore()
    {
        Load();
    }

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

    /// <summary>Picker order: existing files first, then most-launched, then most-recent.</summary>
    public IReadOnlyList<HistoryEntry> SortedForPicker() =>
        Entries
            .OrderByDescending(e => File.Exists(e.ExecutablePath))
            .ThenByDescending(e => e.LaunchCount)
            .ThenByDescending(e => e.LastSeen)
            .ToList();

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

    private void Load()
    {
        try
        {
            if (!File.Exists(StoreFile))
                return;
            var entries = JsonSerializer.Deserialize<List<HistoryEntry>>(File.ReadAllText(StoreFile));
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

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(MotionStore.StoreDirectoryPath);
            File.WriteAllText(StoreFile,
                JsonSerializer.Serialize(Entries, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // best effort; history stays in memory for this session
        }
    }
}
