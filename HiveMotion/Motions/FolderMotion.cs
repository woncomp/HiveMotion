using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace HiveMotion;

/// <summary>
/// A home-layer cell whose activation swaps the whole grid to the folder's own items.
/// Folder layers are never backfilled with scanned windows: only the configured items
/// occupy cells. Nesting (a folder inside a folder) is rejected by store validation
/// and by the manage center. Other home-only child-layer motions are rejected too.
/// </summary>
public sealed class FolderMotion : Motion
{
    public List<Motion> Items { get; set; } = new();

    [JsonIgnore]
    public override string TypeLabel => Loc.Get("Motion_FolderName");

    /// <summary>Follows the bound letter: an unnamed folder is always "Folder {Key}".</summary>
    [JsonIgnore]
    public override string DefaultName => Loc.Format("Folder_DefaultNameFormat", Key);

    [JsonIgnore]
    public override string StatusText => Loc.Plural("Grid_FolderItemCount", Items.Count, Items.Count);

    public override MotionHoverPreview DescribeHover(HiveCell cell) =>
        MotionHoverPreview.Info(EffectiveName, StatusText);
}
