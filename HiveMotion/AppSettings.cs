using System.Collections.Generic;

namespace HiveMotion;

/// <summary>Persisted user settings (settings.json). Lists are mutated in place so
/// consumers holding references (WindowScanner) always see current values.</summary>
public sealed class AppSettings
{
    /// <summary>Process names in pick order: earlier windows choose their cell first.</summary>
    public List<string> PriorityProcessNames { get; set; } = new()
    {
        "msedge",
        "Code"
    };

    /// <summary>Global hotkeys that open the hive while hidden and close it while open.</summary>
    public List<HotkeyRule> Hotkeys { get; set; } = new()
    {
        HotkeyRule.WinTab
    };

    /// <summary>
    /// UI language: "system" follows the OS (non-Chinese falls back to English),
    /// or an explicit culture: "zh-CN" / "en".
    /// </summary>
    public string Language { get; set; } = "system";

    /// <summary>
    /// When true, writes detailed diagnostics to the log file and publishes them to the
    /// live log viewer. Kept off by default to avoid I/O and allocation overhead on the
    /// hot activation path.
    /// </summary>
    public bool VerboseLogging { get; set; }
}
