using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace HiveMotion;

/// <summary>
/// Persists pinned cells to %AppData%/HiveMotion/pins.json so reservations survive
/// restarts. All access happens on the UI thread; the list is mutated in place so
/// consumers holding the reference (CellAssigner) always see current pins.
/// </summary>
public sealed class PinStore
{
    private static readonly string StoreDirectory =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HiveMotion");
    private static readonly string StoreFile = Path.Combine(StoreDirectory, "pins.json");

    /// <summary>Config directory shared by pins.json and history.json.</summary>
    public static string StoreDirectoryPath => StoreDirectory;
    public static string PinsFilePath => StoreFile;

    public List<PinnedApp> Pins { get; } = new();

    public PinStore()
    {
        Load();
    }

    public PinnedApp? FindByKey(char key) => Pins.FirstOrDefault(p => p.Key == key);

    public PinnedApp? FindByIdentity(string executablePath, string arguments) =>
        Pins.FirstOrDefault(p => p.SameIdentityAs(executablePath, arguments));

    public void Set(PinnedApp pin)
    {
        Pins.RemoveAll(p => p.Key == pin.Key);
        Pins.Add(pin);
        Save();
    }

    public void Remove(char key)
    {
        Pins.RemoveAll(p => p.Key == key);
        Save();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(StoreFile))
                return;
            var pins = JsonSerializer.Deserialize<List<PinnedApp>>(File.ReadAllText(StoreFile));
            if (pins == null)
                return;
            Pins.Clear();
            Pins.AddRange(pins.Where(p =>
                p.Key is >= 'A' and <= 'Z' && !string.IsNullOrEmpty(p.ExecutablePath)));
        }
        catch
        {
            // A corrupt store must never block startup; begin with no pins.
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(StoreDirectory);
            File.WriteAllText(StoreFile,
                JsonSerializer.Serialize(Pins, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // best effort; pins stay in memory for this session
        }
    }
}
