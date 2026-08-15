using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace HiveMotion;

/// <summary>
/// Persists home-layer motions to %AppData%/HiveMotion/motions.json so cell
/// reservations survive restarts. All access happens on the UI thread; the list is
/// mutated in place so consumers holding the reference (CellAssigner) always see
/// current motions.
///
/// One-time migration: when motions.json does not exist but a legacy pins.json does,
/// the old flat pin array is converted to application motions and saved as
/// motions.json. The legacy file is left on disk as a rollback backup.
/// </summary>
public sealed class MotionStore
{
    private static readonly string StoreDirectory =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HiveMotion");
    private static readonly string StoreFile = Path.Combine(StoreDirectory, "motions.json");
    private static readonly string LegacyPinsFile = Path.Combine(StoreDirectory, "pins.json");

    /// <summary>Config directory shared by motions.json and history.json.</summary>
    public static string StoreDirectoryPath => StoreDirectory;
    public static string MotionsFilePath => StoreFile;

    /// <summary>Home-layer motions (application and folder cells).</summary>
    public List<Motion> Home { get; } = new();

    public MotionStore()
    {
        Load();
    }

    public Motion? FindByKey(char key) => Home.FirstOrDefault(m => m.Key == key);

    public ApplicationMotion? FindAppByIdentity(string executablePath, string arguments) =>
        Home.OfType<ApplicationMotion>().FirstOrDefault(a => a.SameIdentityAs(executablePath, arguments));

    public void Set(Motion motion)
    {
        Home.RemoveAll(m => m.Key == motion.Key);
        Home.Add(motion);
        Save();
    }

    public void Remove(char key)
    {
        Home.RemoveAll(m => m.Key == key);
        Save();
    }

    /// <summary>Import path: replaces every home-layer motion in one shot.</summary>
    public void ReplaceAll(IEnumerable<Motion> motions)
    {
        Home.Clear();
        Home.AddRange(Sanitize(motions));
        Save();
    }

    /// <summary>Public so in-place child edits (folder contents) can write through.</summary>
    public void Save()
    {
        try
        {
            Directory.CreateDirectory(StoreDirectory);
            File.WriteAllText(StoreFile,
                JsonSerializer.Serialize(Home, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // best effort; motions stay in memory for this session
        }
    }

    private void Load()
    {
        try
        {
            if (File.Exists(StoreFile))
            {
                var motions = JsonSerializer.Deserialize<List<Motion>>(File.ReadAllText(StoreFile));
                if (motions != null)
                {
                    Home.Clear();
                    Home.AddRange(Sanitize(motions));
                }
                return;
            }

            MigrateLegacyPins();
        }
        catch
        {
            // A corrupt store must never block startup; begin with no motions.
        }
    }

    /// <summary>Converts a legacy pins.json into application motions; the old file stays untouched.</summary>
    private void MigrateLegacyPins()
    {
        if (!File.Exists(LegacyPinsFile))
            return;
        try
        {
            var legacy = JsonSerializer.Deserialize<List<LegacyPin>>(File.ReadAllText(LegacyPinsFile));
            if (legacy == null)
                return;
            Home.Clear();
            Home.AddRange(Sanitize(legacy.Select(p => p.ToApplicationMotion())));
            Save();
            Logger.Info($"Migrated {Home.Count} legacy pin(s) from pins.json to motions.json.");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Migrating legacy pins.json");
        }
    }

    /// <summary>
    /// Keeps only placeable motions: A-Z letters, unique per layer, applications with an
    /// executable path, and no folders nested inside folders.
    /// </summary>
    private static List<Motion> Sanitize(IEnumerable<Motion> motions)
    {
        var taken = new HashSet<char>();
        var kept = new List<Motion>();
        foreach (var motion in motions)
        {
            if (motion.Key is < 'A' or > 'Z' || !taken.Add(motion.Key))
                continue;
            if (motion is ApplicationMotion app && string.IsNullOrEmpty(app.ExecutablePath))
                continue;
            if (motion is FolderMotion folder)
                SanitizeFolder(folder);
            kept.Add(motion);
        }
        return kept;
    }

    private static void SanitizeFolder(FolderMotion folder)
    {
        var taken = new HashSet<char>();
        var kept = new List<Motion>();
        foreach (var item in folder.Items)
        {
            if (item is FolderMotion)
            {
                Logger.Warning($"Dropped nested folder '{item.DisplayName}' from folder '{folder.DisplayName}'; folders cannot nest.");
                continue;
            }
            if (item.Key is < 'A' or > 'Z' || !taken.Add(item.Key))
                continue;
            if (item is ApplicationMotion app && string.IsNullOrEmpty(app.ExecutablePath))
                continue;
            kept.Add(item);
        }
        folder.Items = kept;
    }
}

/// <summary>Shape of a pre-Motion pins.json entry; used by store migration and legacy backup import.</summary>
internal sealed class LegacyPin
{
    public char Key { get; set; }
    public string? ProcessName { get; set; }
    public string? ExecutablePath { get; set; }
    public string? Arguments { get; set; }
    public string? WorkingDirectory { get; set; }
    public string? DisplayName { get; set; }

    public ApplicationMotion ToApplicationMotion() => new()
    {
        Key = Key,
        ProcessName = ProcessName ?? string.Empty,
        ExecutablePath = ExecutablePath ?? string.Empty,
        Arguments = Arguments ?? string.Empty,
        WorkingDirectory = WorkingDirectory ?? string.Empty,
        DisplayName = DisplayName ?? string.Empty
    };
}
