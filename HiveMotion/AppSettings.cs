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

    /// <summary>
    /// Global hotkeys that summon the hive. First press (overlay hidden) opens the overlay;
    /// second press behavior is controlled by <see cref="SecondPressPassthrough"/>.
    /// </summary>
    public List<HotkeyRule> Hotkeys { get; set; } = new()
    {
        HotkeyRule.WinTab
    };

    /// <summary>
    /// True: a second press while the overlay is open falls through to the combo's native
    /// system function (Task View). False: the second press is swallowed and only closes.
    /// </summary>
    public bool SecondPressPassthrough { get; set; } = true;

    /// <summary>
    /// UI language: "system" follows the OS (non-Chinese falls back to English),
    /// or an explicit culture: "zh-CN" / "en".
    /// </summary>
    public string Language { get; set; } = "system";
}
