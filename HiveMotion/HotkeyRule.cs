namespace HiveMotion;

/// <summary>
/// One global hotkey combo. Generic rule for every registered hotkey:
/// while the overlay is hidden the combo is swallowed and opens it; while the overlay is
/// open the combo passes through to the system so its native function (Task View, Game Bar…)
/// fires, and the overlay closes itself.
/// </summary>
public sealed class HotkeyRule
{
    public bool Win { get; init; }
    public bool Ctrl { get; init; }
    public bool Alt { get; init; }
    public bool Shift { get; init; }
    public int Vk { get; init; }
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Window class names of this combo's native UI (e.g. Task View). When that UI is the
    /// foreground window, the combo is passed through untouched so the native UI toggles
    /// itself closed instead of reopening our overlay.
    /// </summary>
    public string[] NativeClassNames { get; init; } = System.Array.Empty<string>();

    /// <summary>Same escape hatch, matched on the foreground window's process name (e.g. Game Bar).</summary>
    public string[] NativeProcessNames { get; init; } = System.Array.Empty<string>();

    public static HotkeyRule WinTab => new()
    {
        Win = true,
        Vk = NativeMethods.VK_TAB,
        Name = "Win+Tab",
        // MultitaskingViewFrame: legacy Task View host; XamlExplorerHostIslandWindow: the
        // XAML island hosting Task View (and other shell surfaces) on current Win11 builds.
        NativeClassNames = new[] { "MultitaskingViewFrame", "XamlExplorerHostIslandWindow" }
    };

    public static HotkeyRule WinG => new()
    {
        Win = true,
        Vk = 0x47,
        Name = "Win+G",
        NativeProcessNames = new[] { "GameBar" }
    };
}
