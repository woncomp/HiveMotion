using System;
using System.Windows.Media;

namespace HiveMotion;

/// <summary>A letter cell of the hive grid: a running window, or a motion reserving the cell.</summary>
public sealed class HiveCell
{
    public char Letter { get; set; }
    public string AppName { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public ImageSource? Icon { get; set; }
    public IntPtr WindowHandle { get; set; }
    public uint ProcessId { get; set; }
    public long ProcessCreationFileTime { get; set; }
    public string ProcessName { get; set; } = string.Empty;
    /// <summary>Full image path of the owning process, captured at scan time; null when unreadable or not running.</summary>
    public string? ExecutablePath { get; set; }
    /// <summary>Command line argument tail with original quoting; null when unreadable or not running.</summary>
    public string? CommandLineArguments { get; set; }
    /// <summary>The motion occupying this cell; null for plain scanned windows.</summary>
    public Motion? Motion { get; set; }

    public bool IsRunning => WindowHandle != IntPtr.Zero;
    /// <summary>Cell reserved by an application motion (the old "pinned" semantics).</summary>
    public bool IsPinned => Motion is ApplicationMotion;
    public ApplicationMotion? Application => Motion as ApplicationMotion;
    public FolderMotion? Folder => Motion as FolderMotion;
    public SystemActionMotion? SystemAction => Motion as SystemActionMotion;

    public static HiveCell FromWindow(char letter, RunningWindow window) => new()
    {
        Letter = letter,
        AppName = window.AppName,
        Title = window.Title,
        Icon = window.Icon,
        WindowHandle = window.Handle,
        ProcessId = window.ProcessId,
        ProcessCreationFileTime = window.ProcessCreationFileTime,
        ProcessName = window.ProcessName,
        ExecutablePath = window.ExecutablePath,
        CommandLineArguments = window.CommandLineArguments
    };
}
