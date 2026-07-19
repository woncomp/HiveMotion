using System;
using System.Windows.Media;

namespace HiveMotion;

/// <summary>A letter cell of the hive grid: a running window, or a pinned app reserved for relaunch.</summary>
public sealed class HiveCell
{
    public char Letter { get; set; }
    public string AppName { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public ImageSource? Icon { get; set; }
    public IntPtr WindowHandle { get; set; }
    public uint ProcessId { get; set; }
    public string ProcessName { get; set; } = string.Empty;
    public PinnedApp? Pin { get; set; }

    public bool IsRunning => WindowHandle != IntPtr.Zero;
    public bool IsPinned => Pin != null;

    public static HiveCell FromWindow(char letter, RunningWindow window) => new()
    {
        Letter = letter,
        AppName = window.AppName,
        Title = window.Title,
        Icon = window.Icon,
        WindowHandle = window.Handle,
        ProcessId = window.ProcessId,
        ProcessName = window.ProcessName
    };
}
