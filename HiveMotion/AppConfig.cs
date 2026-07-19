using System.Collections.Generic;

namespace HiveMotion;

/// <summary>User-tunable configuration: the window priority queue and summon hotkeys.</summary>
public sealed class AppConfig
{
    /// <summary>Process names in pick order: earlier windows choose their cell first.</summary>
    public List<string> PriorityProcessNames { get; } = new()
    {
        "msedge",
        "Code"
    };

    /// <summary>
    /// Global hotkeys that summon the hive. First press (overlay hidden) opens the overlay;
    /// second press (overlay open) falls through to the combo's native system function.
    /// </summary>
    public List<HotkeyRule> Hotkeys { get; } = new()
    {
        HotkeyRule.WinTab
    };

    public static AppConfig Default { get; } = new();
}
