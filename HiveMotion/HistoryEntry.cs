using System;

namespace HiveMotion;

/// <summary>
/// One observed launch identity: a program plus its argument set, seen in at least one
/// hive scan. LaunchCount ranks entries in the picker — identities that newly appear in
/// a scan (were absent from the previous one) count as a fresh launch.
/// </summary>
public sealed class HistoryEntry
{
    public string ProcessName { get; set; } = string.Empty;
    public string ExecutablePath { get; set; } = string.Empty;
    public string Arguments { get; set; } = string.Empty;
    public string WorkingDirectory { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public int LaunchCount { get; set; } = 1;
    public DateTime FirstSeen { get; set; }
    public DateTime LastSeen { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public string CommandLine =>
        string.IsNullOrEmpty(Arguments) ? ExecutablePath : $"{ExecutablePath} {Arguments}";

    public string IdentityKey => Key(ExecutablePath, Arguments);

    /// <summary>Case/whitespace-insensitive identity, shared by history and pin matching.</summary>
    public static string Key(string executablePath, string? arguments) =>
        executablePath.ToLowerInvariant() + "\u0001" +
        ApplicationMotion.NormalizeArguments(arguments).ToLowerInvariant();
}
