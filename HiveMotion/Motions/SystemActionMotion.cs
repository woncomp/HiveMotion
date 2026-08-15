using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace HiveMotion;

/// <summary>How a system action is fired. Simulated keys are never used.</summary>
public enum SystemActionKind
{
    /// <summary>explorer.exe shell:::{CLSID} — opens a shell object (Task View, Run dialog).</summary>
    ShellObject,
    /// <summary>Shell-open a target: a protocol URI (ms-gamebar:, ms-settings:) or a plain executable.</summary>
    ShellOpen,
    /// <summary>user32 LockWorkStation().</summary>
    Lock
}

/// <summary>
/// One built-in system action: a stable id, a Segoe MDL2 glyph, and a concrete trigger.
/// Display name and description are localized via <see cref="NameKey"/>/<see cref="DescriptionKey"/>;
/// nothing user-facing is stored on the motion.
/// </summary>
public sealed class SystemAction
{
    public required string Id { get; init; }
    /// <summary>Segoe MDL2 Assets codepoint, e.g. "\uE713".</summary>
    public required string IconGlyph { get; init; }
    /// <summary>Resource key for the localized short name shown on cells.</summary>
    public required string NameKey { get; init; }
    /// <summary>Resource key for the localized one-line description.</summary>
    public required string DescriptionKey { get; init; }
    public required SystemActionKind Kind { get; init; }
    /// <summary>shell::: argument for ShellObject, URI/path for ShellOpen; empty for Lock.</summary>
    public string Target { get; init; } = string.Empty;
}

/// <summary>
/// Static catalog of assignable system actions. A <see cref="SystemActionMotion"/> only
/// stores an action id; execution logic, icon, and name all resolve through this catalog
/// (the Stream Deck model: the per-cell choice is which action, nothing else).
/// </summary>
public static class SystemActions
{
    public static readonly SystemAction TaskView = new()
    {
        Id = "taskview",
        IconGlyph = "\uE7C4",
        NameKey = "SystemAction_TaskView_Name",
        DescriptionKey = "SystemAction_TaskView_Desc",
        Kind = SystemActionKind.ShellObject,
        Target = "shell:::{3080F90E-D7AD-11D9-BD98-0000947B0257}"
    };

    public static readonly SystemAction GameBar = new()
    {
        Id = "gamebar",
        IconGlyph = "\uE7FC",
        NameKey = "SystemAction_GameBar_Name",
        DescriptionKey = "SystemAction_GameBar_Desc",
        Kind = SystemActionKind.ShellOpen,
        Target = "ms-gamebar:"
    };

    public static readonly SystemAction Settings = new()
    {
        Id = "settings",
        IconGlyph = "\uE713",
        NameKey = "SystemAction_Settings_Name",
        DescriptionKey = "SystemAction_Settings_Desc",
        Kind = SystemActionKind.ShellOpen,
        Target = "ms-settings:"
    };

    public static readonly SystemAction Explorer = new()
    {
        Id = "explorer",
        IconGlyph = "\uEC50",
        NameKey = "SystemAction_Explorer_Name",
        DescriptionKey = "SystemAction_Explorer_Desc",
        Kind = SystemActionKind.ShellOpen,
        Target = "explorer.exe"
    };

    public static readonly SystemAction Project = new()
    {
        Id = "project",
        IconGlyph = "\uE7F4",
        NameKey = "SystemAction_Project_Name",
        DescriptionKey = "SystemAction_Project_Desc",
        Kind = SystemActionKind.ShellOpen,
        Target = "DisplaySwitch.exe"
    };

    public static readonly SystemAction Lock = new()
    {
        Id = "lock",
        IconGlyph = "\uE72E",
        NameKey = "SystemAction_Lock_Name",
        DescriptionKey = "SystemAction_Lock_Desc",
        Kind = SystemActionKind.Lock
    };

    /// <summary>Picker order in the manage center.</summary>
    public static IReadOnlyList<SystemAction> All { get; } = new[] { TaskView, GameBar, Settings, Explorer, Project, Lock };

    public static SystemAction? Find(string? id) =>
        string.IsNullOrEmpty(id) ? null : All.FirstOrDefault(a => a.Id == id);

    /// <summary>Localized display name; falls back to the raw id for unknown actions.</summary>
    public static string DisplayNameOf(string actionId) =>
        Find(actionId) is { } action ? Loc.Get(action.NameKey) : actionId;

    /// <summary>Localized one-line description; empty for unknown actions.</summary>
    public static string DescriptionOf(string actionId) =>
        Find(actionId) is { } action ? Loc.Get(action.DescriptionKey) : string.Empty;
}

/// <summary>
/// A cell bound to one built-in system action (Task View, Game Bar, Settings, Explorer,
/// Project, Lock). The action is chosen at configuration time — the Stream Deck model:
/// execution logic, icon, and name all come from the <see cref="SystemActions"/> catalog,
/// so the only persisted choice is <see cref="ActionId"/>. It never matches windows and
/// is never "running"; activation fires the action immediately.
/// </summary>
public sealed class SystemActionMotion : Motion
{
    /// <summary>Id into the <see cref="SystemActions"/> catalog, e.g. "taskview".</summary>
    public string ActionId { get; set; } = string.Empty;

    public override MotionHoverPreview DescribeHover(HiveCell cell) =>
        MotionHoverPreview.Info(SystemActions.DisplayNameOf(ActionId), SystemActions.DescriptionOf(ActionId));

    /// <summary>
    /// Fires the configured action through its real invocation path — a shell object
    /// (explorer.exe shell:::{CLSID}), a protocol URI / plain shell-open, or the
    /// LockWorkStation API. No key simulation: our own hotkey rules never interfere.
    /// </summary>
    public void Activate()
    {
        if (SystemActions.Find(ActionId) is not { } action)
        {
            Logger.Warning($"System action cell '{Key}' references unknown action id '{ActionId}'.");
            return;
        }

        try
        {
            switch (action.Kind)
            {
                case SystemActionKind.ShellObject:
                    Process.Start(new ProcessStartInfo("explorer.exe")
                    {
                        Arguments = action.Target,
                        UseShellExecute = true
                    });
                    break;
                case SystemActionKind.ShellOpen:
                    Process.Start(new ProcessStartInfo(action.Target) { UseShellExecute = true });
                    break;
                case SystemActionKind.Lock:
                    NativeMethods.LockWorkStation();
                    break;
            }
            Logger.Info($"Triggered system action '{action.Id}' ({action.Kind}).");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"Triggering system action '{action.Id}'");
        }
    }
}
