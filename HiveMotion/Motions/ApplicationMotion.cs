using System;
using System.IO;
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
    private string _arguments = string.Empty;
    /// <summary>Argument tail with original quoting; empty means "match any arguments".</summary>
    public string Arguments
    {
        get => _arguments;
        set
        {
            _arguments = value;
            NormalizedArguments = NormalizeArguments(value);
        }
    }
    /// <summary>Cached normalized form of <see cref="Arguments"/>; recomputed on every write.</summary>
    internal string NormalizedArguments { get; private set; } = string.Empty;
    public string WorkingDirectory { get; set; } = string.Empty;

    [JsonIgnore]
    public override bool IsConfigured => !string.IsNullOrWhiteSpace(ExecutablePath);

    [JsonIgnore]
    public override string TypeLabel => Loc.Get("Motion_ApplicationName");

    [JsonIgnore]
    public override string DefaultName
    {
        get
        {
            if (IsConfigured && Path.GetFileNameWithoutExtension(ExecutablePath) is { Length: > 0 } fileName)
                return fileName;
            return Loc.Get("Motion_ApplicationName");
        }
    }

    [JsonIgnore]
    public override string StatusText => IsConfigured ? string.Empty : Loc.Get("Motion_NotConfigured");

    [JsonIgnore]
    public string CommandLine =>
        string.IsNullOrEmpty(Arguments) ? ExecutablePath : $"{ExecutablePath} {Arguments}";

    public bool Matches(RunningWindow window)
    {
        if (!IsConfigured)
            return false;
        if (window.ExecutablePath == null)
            return false;
        if (!string.Equals(window.ExecutablePath, ExecutablePath, StringComparison.OrdinalIgnoreCase))
            return false;
        if (Arguments.Length == 0)
            return true; // captured without arguments: exe-only identity
        // The window's normalized form is computed once on the scanner thread.
        return string.Equals(
            window.NormalizedArguments,
            NormalizedArguments,
            StringComparison.OrdinalIgnoreCase);
    }

    public bool SameIdentityAs(string executablePath, string arguments) =>
        string.Equals(executablePath, ExecutablePath, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(NormalizeArguments(arguments), NormalizedArguments, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whitespace-insensitive form so trailing/duplicate spaces never break equality.</summary>
    public static string NormalizeArguments(string? arguments) =>
        string.IsNullOrWhiteSpace(arguments)
            ? string.Empty
            : string.Join(' ', arguments.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>Running: the window's live DWM thumbnail; not running: the launch identity.</summary>
    public override MotionHoverPreview DescribeHover(HiveCell cell)
    {
        if (!IsConfigured)
            return MotionHoverPreview.Info(Loc.Get("Motion_ApplicationName"), Loc.Get("Motion_NotConfigured"));
        if (cell.IsRunning)
            return MotionHoverPreview.Thumbnail;

        string detail = string.IsNullOrEmpty(WorkingDirectory)
            ? CommandLine
            : Loc.Format("Grid_LaunchInfoWithDir", CommandLine, WorkingDirectory);
        return MotionHoverPreview.Info(EffectiveName, detail);
    }
}
