using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;

namespace HiveMotion;

/// <summary>
/// A home-only entry into a dynamic layer populated from the latest window snapshot.
/// Empty executable filters mean all candidate windows; configured filters compare
/// executable file names only and deliberately ignore paths and command-line arguments.
/// </summary>
public sealed class WindowViewMotion : Motion
{
    public List<string> ExecutableNames { get; set; } = new();

    public bool Matches(RunningWindow window)
    {
        if (ExecutableNames.Count == 0)
            return true;

        string? candidate = NormalizeExecutableName(window.ExecutablePath);
        if (candidate == null)
            candidate = NormalizeExecutableName(window.ProcessName);
        return candidate != null && ExecutableNames.Contains(candidate, StringComparer.OrdinalIgnoreCase);
    }

    public void NormalizeExecutableNames()
    {
        ExecutableNames ??= new List<string>();
        ExecutableNames = ExecutableNames
            .Select(NormalizeExecutableName)
            .Where(name => name != null)
            .Select(name => name!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static string? NormalizeExecutableName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        string trimmed = value.Trim().Trim('"').Trim();
        if (trimmed.Length == 0)
            return null;

        string fileName;
        try
        {
            fileName = Path.GetFileName(trimmed);
        }
        catch (ArgumentException)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(fileName))
            return null;
        if (Path.GetExtension(fileName).Length == 0)
            fileName += ".exe";
        return fileName;
    }

    [JsonIgnore]
    public override string TypeLabel => Loc.Get("Motion_WindowViewName");

    [JsonIgnore]
    public override string DefaultName => Loc.Get("Motion_WindowViewName");

    [JsonIgnore]
    public override string StatusText =>
        ExecutableNames.Count == 0
            ? Loc.Get("WindowView_AllApplications")
            : Loc.Plural("WindowView_ApplicationCount", ExecutableNames.Count, ExecutableNames.Count);

    public override MotionHoverPreview DescribeHover(HiveCell cell) =>
        MotionHoverPreview.Info(EffectiveName, StatusText);
}
