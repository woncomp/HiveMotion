using System.Collections.Generic;

namespace HiveMotion;

/// <summary>User-tunable configuration: pinned actions and the window priority queue.</summary>
public sealed class AppConfig
{
    /// <summary>Preset actions pinned to a fixed letter, filled in before any window is placed.</summary>
    public List<AppItem> Presets { get; } = new()
    {
        new AppItem
        {
            Key = 'N',
            ProcessName = "notepad",
            ExecutablePath = "notepad.exe",
            DisplayName = "记事本",
            IconGlyph = "📝"
        }
    };

    /// <summary>Process names in pick order: earlier windows choose their cell first.</summary>
    public List<string> PriorityProcessNames { get; } = new()
    {
        "msedge",
        "Code"
    };

    public static AppConfig Default { get; } = new();
}
