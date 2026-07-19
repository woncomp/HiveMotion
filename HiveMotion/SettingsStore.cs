using System;
using System.IO;
using System.Text.Json;

namespace HiveMotion;

/// <summary>Loads and saves settings.json under the shared config directory.</summary>
public sealed class SettingsStore
{
    private static readonly string StoreFile =
        Path.Combine(PinStore.StoreDirectoryPath, "settings.json");

    public AppSettings Settings { get; private set; } = new();

    public SettingsStore()
    {
        Load();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(PinStore.StoreDirectoryPath);
            File.WriteAllText(StoreFile,
                JsonSerializer.Serialize(Settings, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // best effort; settings stay in memory for this session
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(StoreFile))
                return;
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(StoreFile));
            if (settings != null)
                Settings = settings;
        }
        catch
        {
            // A corrupt store must never block startup; begin with defaults.
        }
    }
}
