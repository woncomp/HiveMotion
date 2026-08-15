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
/// the Motions folder) and become placeable on the home layer and inside folders
/// without model changes.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(ApplicationMotion), "application")]
[JsonDerivedType(typeof(FolderMotion), "folder")]
[JsonDerivedType(typeof(SystemActionMotion), "systemaction")]
public abstract class Motion
{
    /// <summary>Letter (A-Z) this motion occupies on its layer (home, or inside a folder).</summary>
    public char Key { get; set; }
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Declares what the hover preview area shows for the cell holding this motion.</summary>
    public abstract MotionHoverPreview DescribeHover(HiveCell cell);
}
