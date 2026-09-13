using System.Text.Json.Serialization;

namespace HiveMotion;

/// <summary>What the hover preview area should show for a cell holding a motion.</summary>
public enum MotionHoverKind
{
    None,
    WindowThumbnail,
    Info
}

/// <summary>
/// Lightweight hover-preview description produced by a motion. Pure data: the model
/// layer declares WHAT to show, the overlay decides HOW to render it (DWM interop
/// stays in TaskGridView / DwmThumbnailPreview).
/// </summary>
public sealed class MotionHoverPreview
{
    public MotionHoverKind Kind { get; private init; }
    public string Title { get; private init; } = string.Empty;
    public string Detail { get; private init; } = string.Empty;

    public static readonly MotionHoverPreview None = new() { Kind = MotionHoverKind.None };
    public static readonly MotionHoverPreview Thumbnail = new() { Kind = MotionHoverKind.WindowThumbnail };

    public static MotionHoverPreview Info(string title, string detail) =>
        new() { Kind = MotionHoverKind.Info, Title = title, Detail = detail };
}

/// <summary>
/// Anything that can occupy a letter cell of the hive grid. Motions are the content,
/// cells are the containers. New motion kinds extend this type (one file per kind in
/// the Motions folder); store and editor validation define which layers accept them.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(ApplicationMotion), "application")]
[JsonDerivedType(typeof(FolderMotion), "folder")]
[JsonDerivedType(typeof(SystemActionMotion), "systemaction")]
[JsonDerivedType(typeof(WindowViewMotion), "windowview")]
public abstract class Motion
{
    /// <summary>Letter (A-Z) this motion occupies on its layer (home, or inside a folder).</summary>
    public char Key { get; set; }
    /// <summary>User-assigned custom name; empty resolves to the kind's <see cref="DefaultName"/>.</summary>
    public string DisplayName { get; set; } = string.Empty;
    /// <summary>Optional custom icon path; empty uses the motion kind's default icon.</summary>
    public string IconPath { get; set; } = string.Empty;

    /// <summary>Whether activation has enough configuration to perform the motion.</summary>
    [JsonIgnore]
    public virtual bool IsConfigured => true;

    /// <summary>Localized human-readable label for the motion kind (the palette name).</summary>
    [JsonIgnore]
    public abstract string TypeLabel { get; }

    /// <summary>Kind-specific fallback name used while <see cref="DisplayName"/> is empty.</summary>
    [JsonIgnore]
    public abstract string DefaultName { get; }

    /// <summary>The name shown for this motion: the custom name when set, else the kind default.</summary>
    [JsonIgnore]
    public string EffectiveName => string.IsNullOrWhiteSpace(DisplayName) ? DefaultName : DisplayName;

    /// <summary>Optional one-line kind status shown after <see cref="TypeLabel"/> in the shared editor header.</summary>
    [JsonIgnore]
    public virtual string StatusText => string.Empty;

    /// <summary>Declares what the hover preview area shows for the cell holding this motion.</summary>
    public abstract MotionHoverPreview DescribeHover(HiveCell cell);
}
