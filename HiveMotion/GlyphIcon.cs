using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace HiveMotion;

/// <summary>
/// Renders Segoe MDL2 Assets glyphs to frozen, resolution-independent DrawingImages
/// (32 px em, honey-gold). Cached per codepoint: cells and picker rows share one
/// instance per glyph, and the frozen geometry costs nothing per frame.
/// </summary>
internal static class GlyphIcon
{
    private const double EmSize = 32;
    private static readonly Dictionary<string, ImageSource> Cache = new(System.StringComparer.Ordinal);

    private static readonly Typeface IconTypeface = new(
        new FontFamily("Segoe MDL2 Assets"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    private static readonly Brush GlyphBrush = CreateBrush();

    private static Brush CreateBrush()
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFD97A"));
        brush.Freeze();
        return brush;
    }

    /// <summary>Glyph image for a system action id; null when the action is unknown.</summary>
    public static ImageSource? ForAction(string actionId) =>
        SystemActions.Find(actionId) is { } action ? ForGlyph(action.IconGlyph) : null;

    public static ImageSource ForGlyph(string glyph)
    {
        if (Cache.TryGetValue(glyph, out var cached))
            return cached;

        // Geometry (not a bitmap): the image stays crisp at any DPI and render scale.
        var text = new FormattedText(glyph, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            IconTypeface, EmSize, GlyphBrush, pixelsPerDip: 1.0);
        var image = new DrawingImage(new GeometryDrawing(GlyphBrush, null, text.BuildGeometry(new Point(0, 0))));
        image.Freeze();
        Cache[glyph] = image;
        return image;
    }
}
