using System;
using System.Text.Json.Serialization;

namespace HiveMotion;

/// <summary>
/// A cell reserved for one launch identity: an executable plus its argument set.
/// The cell only ever matches windows of the same program launched with the same
/// arguments, and can relaunch that command when the program is not running.
/// (This is the semantic successor of the old pinned app.)
/// </summary>
public sealed class ApplicationMotion : Motion
{
    public string ProcessName { get; set; } = string.Empty;
    public string ExecutablePath { get; set; } = string.Empty;
    /// <summary>Argument tail with original quoting; empty means "match any arguments".</summary>
    public string Arguments { get; set; } = string.Empty;
    public string WorkingDirectory { get; set; } = string.Empty;

    [JsonIgnore]
    public string CommandLine =>
        string.IsNullOrEmpty(Arguments) ? ExecutablePath : $"{ExecutablePath} {Arguments}";

    public bool Matches(RunningWindow window)
    {
        if (window.ExecutablePath == null)
            return false;
        if (!string.Equals(window.ExecutablePath, ExecutablePath, StringComparison.OrdinalIgnoreCase))
            return false;
        if (Arguments.Length == 0)
            return true; // captured without arguments: exe-only identity
        return string.Equals(
            NormalizeArguments(window.CommandLineArguments),
            NormalizeArguments(Arguments),
            StringComparison.OrdinalIgnoreCase);
    }

    public bool SameIdentityAs(string executablePath, string arguments) =>
        string.Equals(executablePath, ExecutablePath, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(NormalizeArguments(arguments), NormalizeArguments(Arguments), StringComparison.OrdinalIgnoreCase);

    /// <summary>Whitespace-insensitive form so trailing/duplicate spaces never break equality.</summary>
    public static string NormalizeArguments(string? arguments) =>
        string.IsNullOrWhiteSpace(arguments)
            ? string.Empty
            : string.Join(' ', arguments.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>Running: the window's live DWM thumbnail; not running: the launch identity.</summary>
    public override MotionHoverPreview DescribeHover(HiveCell cell)
    {
        if (cell.IsRunning)
            return MotionHoverPreview.Thumbnail;

        string detail = string.IsNullOrEmpty(WorkingDirectory)
            ? CommandLine
            : Loc.Format("Grid_LaunchInfoWithDir", CommandLine, WorkingDirectory);
        return MotionHoverPreview.Info(DisplayName, detail);
    }
}
