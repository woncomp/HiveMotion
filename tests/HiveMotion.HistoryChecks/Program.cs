using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using HiveMotion;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        var tests = new (string Name, Action Run)[]
        {
            ("Repeated scans coalesce into bounded background writes", CoalescedWrites),
            ("A mutation after Save is invisible to the in-flight snapshot", SnapshotIsolation),
            ("A failed save leaves the in-memory history intact", FailedSaveKeepsMemory),
            ("Picker ordering puts existing executables first once the cache refreshes", ExistenceCacheOrdering)
        };
        try
        {
            foreach (var test in tests)
            {
                test.Run();
                Console.WriteLine($"PASS {test.Name}");
            }
            Console.WriteLine($"{tests.Length} history checks passed. No interactive windows were shown.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { Logger.Shutdown(); }
    }

    private static void CoalescedWrites()
    {
        using var dir = new TempDir();
        var store = new HistoryStore(dir.File("history.json"));
        store.RecordScan(Windows((Path.Combine(dir.Path, "app-seed.exe"), null)));
        store.Flush();
        Assert(store.WritesCompleted == 1, $"first scan should write once, wrote {store.WritesCompleted}");

        for (int i = 0; i < 25; i++)
            store.RecordScan(Windows((Path.Combine(dir.Path, $"app-{i}.exe"), null)));
        store.Flush();

        Assert(store.WritesCompleted <= 4,
            $"25 rapid scans should coalesce into at most a few writes, total={store.WritesCompleted}");
        Assert(store.Entries.Count == 26, $"expected 26 entries, got {store.Entries.Count}");
        string json = File.ReadAllText(dir.File("history.json"));
        Assert(json.Contains("app-24"), "the newest identity must survive coalescing");
        var parsed = System.Text.Json.JsonSerializer.Deserialize<List<HistoryEntry>>(json);
        Assert(parsed?.Count == 26, "written JSON must parse with all entries");
    }

    private static void SnapshotIsolation()
    {
        using var dir = new TempDir();
        var store = new HistoryStore(dir.File("history.json"), TimeSpan.FromMilliseconds(400));
        store.RecordScan(Windows((Path.Combine(dir.Path, "slow-a.exe"), null)));
        store.RecordScan(Windows((Path.Combine(dir.Path, "slow-b.exe"), null)));
        // Mutate after Save() has taken its snapshot; the writer must serialize the snapshot.
        store.Entries.First(e => e.ExecutablePath.EndsWith("slow-a.exe", StringComparison.Ordinal)).DisplayName = "MUTATED-AFTER-SNAPSHOT";
        store.Flush();

        string json = File.ReadAllText(dir.File("history.json"));
        Assert(!json.Contains("MUTATED-AFTER-SNAPSHOT"), "in-flight write must not observe post-Save mutations");
        Assert(json.Contains("slow-a.exe") && json.Contains("slow-b.exe"),
            "both identities must be present after the in-flight save");
        var parsed = System.Text.Json.JsonSerializer.Deserialize<List<HistoryEntry>>(json);
        Assert(parsed?.Count == 2, "written JSON must stay well-formed across overlapping saves");
    }

    private static void FailedSaveKeepsMemory()
    {
        using var dir = new TempDir();
        // A regular file where the store directory must be: CreateDirectory/WriteAllText will fail.
        string blocker = dir.File("blocker");
        File.WriteAllText(blocker, "not a directory");
        var store = new HistoryStore(Path.Combine(blocker, "history.json"));

        store.RecordScan(Windows((Path.Combine(dir.Path, "survivor.exe"), null)));
        store.Flush();

        Assert(store.Entries.Count == 1, $"in-memory history must survive a failed save, got {store.Entries.Count}");
        Assert(store.Entries[0].LaunchCount == 1, "entry content must stay intact after the failed save");
        Assert(store.Entries[0].ExecutablePath.EndsWith("survivor.exe", StringComparison.Ordinal),
            "entry identity must stay intact after the failed save");
    }

    private static void ExistenceCacheOrdering()
    {
        using var dir = new TempDir();
        string existing = dir.File("existing.exe");
        File.WriteAllText(existing, string.Empty);
        string missing = dir.File("missing.exe"); // never created

        var store = new HistoryStore(dir.File("history.json"));
        // missing reappears after an absence, so it earns the higher launch count; without
        // the existence preference it would sort ahead of the existing executable.
        store.RecordScan(Windows((missing, null)));
        store.RecordScan(Windows((existing, null)));
        store.RecordScan(Windows((missing, null)));
        Assert(store.Entries.First(e => e.ExecutablePath == missing).LaunchCount == 2,
            "setup: missing.exe should have the higher launch count");
        Assert(store.Entries.First(e => e.ExecutablePath == existing).LaunchCount == 1,
            "setup: existing.exe should have the lower launch count");

        var deadline = Stopwatch.StartNew();
        IReadOnlyList<HistoryEntry> ordered = store.SortedForPicker();
        while (ordered[0].ExecutablePath != existing && deadline.Elapsed < TimeSpan.FromSeconds(3))
        {
            Thread.Sleep(20);
            ordered = store.SortedForPicker();
        }
        Assert(ordered[0].ExecutablePath == existing,
            "after the background refresh, the existing executable must sort first");
    }

    private static RunningWindow[] Windows(params (string Path, string? Args)[] identities) =>
        identities.Select(i => new RunningWindow
        {
            ProcessName = Path.GetFileNameWithoutExtension(i.Path),
            AppName = Path.GetFileNameWithoutExtension(i.Path),
            ExecutablePath = i.Path,
            CommandLineArguments = i.Args,
            WorkingDirectory = string.Empty
        }).ToArray();

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "hm-hist-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }
        public string File(string name) => System.IO.Path.Combine(Path, name);

        public void Dispose()
        {
            try { Directory.Delete(Path, true); } catch { /* best effort */ }
        }
    }
}
