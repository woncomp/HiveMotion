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
    /// <summary>Full image path of the owning process, captured at scan time; null when unreadable or not running.</summary>
    public string? ExecutablePath { get; set; }
    /// <summary>Command line argument tail with original quoting; null when unreadable or not running.</summary>
    public string? CommandLineArguments { get; set; }
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
        ProcessName = window.ProcessName,
        ExecutablePath = window.ExecutablePath,
        CommandLineArguments = window.CommandLineArguments
    };
}
