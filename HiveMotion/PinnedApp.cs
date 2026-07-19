using System;

namespace HiveMotion;

/// <summary>
/// A cell reserved for one launch identity: an executable plus its argument set.
/// The cell only ever matches windows of the same program launched with the same
/// arguments, and can relaunch that command when the program is not running.
/// </summary>
public sealed class PinnedApp
{
    public char Key { get; set; }
    public string ProcessName { get; set; } = string.Empty;
    public string ExecutablePath { get; set; } = string.Empty;
    /// <summary>Argument tail with original quoting; empty means "match any arguments".</summary>
    public string Arguments { get; set; } = string.Empty;
    public string WorkingDirectory { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;

    [System.Text.Json.Serialization.JsonIgnore]
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
}
