using System.Collections.Generic;

namespace HiveMotion;

/// <summary>
/// A home-layer cell whose activation swaps the whole grid to the folder's own items.
/// Folder layers are never backfilled with scanned windows: only the configured items
/// occupy cells. Nesting (a folder inside a folder) is rejected by store validation
/// and by the manage center, not by the item type, so future motion kinds stay
/// placeable inside folders.
/// </summary>
public sealed class FolderMotion : Motion
{
    /// <summary>Optional custom icon image (png/ico/jpg/exe); empty falls back to the name initial.</summary>
    public string IconPath { get; set; } = string.Empty;
    public List<Motion> Items { get; set; } = new();

    public override MotionHoverPreview DescribeHover(HiveCell cell) =>
        MotionHoverPreview.Info(DisplayName, Loc.Plural("Grid_FolderItemCount", Items.Count, Items.Count));
}
