namespace HiveMotion;

/// <summary>
/// One global hotkey combo. Generic rule for every registered hotkey:
/// while the overlay is hidden the combo is swallowed and opens it; while the overlay is
/// open the combo is swallowed and closes it.
/// </summary>
public sealed class HotkeyRule
{
    public bool Win { get; init; }
    public bool Ctrl { get; init; }
    public bool Alt { get; init; }
    public bool Shift { get; init; }
    public int Vk { get; init; }
    public string Name { get; init; } = string.Empty;

    public static HotkeyRule WinTab => new()
    {
        Win = true,
        Vk = NativeMethods.VK_TAB,
        Name = "Win+Tab"
    };

    public static HotkeyRule WinG => new()
    {
        Win = true,
        Vk = 0x47,
        Name = "Win+G"
    };
}
