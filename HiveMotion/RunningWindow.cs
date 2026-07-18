using System;
using System.Windows.Media;

namespace HiveMotion;

public sealed class RunningWindow
{
    public IntPtr Handle { get; set; }
    public uint ProcessId { get; set; }
    public string ProcessName { get; set; } = string.Empty;
    public string AppName { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public ImageSource? Icon { get; set; }
    /// <summary>Static snapshot of the window contents; null when capture failed or minimized.</summary>
    public ImageSource? Preview { get; set; }
    /// <summary>Position in the user-configured priority queue; lower picks a cell first.</summary>
    public int Priority { get; set; } = 100;
    /// <summary>Top-level z-order index from EnumWindows (0 = foreground-most).</summary>
    public int ZOrder { get; set; }
    /// <summary>Letter this window wants, derived from its app name; null when no A-Z letter exists.</summary>
    public char? PreferredLetter { get; set; }
}
