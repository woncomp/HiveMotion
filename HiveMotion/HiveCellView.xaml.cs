using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace HiveMotion;

public partial class HiveCellView : System.Windows.Controls.UserControl
{
    private HiveCell? _cell;

    public event EventHandler<HiveCell>? Clicked;
    public event EventHandler<HiveCell>? Hovered;
    public event EventHandler<HiveCell>? Unhovered;

    public HiveCellView()
    {
        InitializeComponent();
        MouseEnter += (_, _) =>
        {
            SetHover(true);
            if (_cell != null)
                Hovered?.Invoke(this, _cell);
        };
        MouseLeave += (_, _) =>
        {
            SetHover(false);
            if (_cell != null)
                Unhovered?.Invoke(this, _cell);
        };
        MouseLeftButtonUp += (_, e) =>
        {
            if (_cell != null)
                Clicked?.Invoke(this, _cell);
            e.Handled = true;
        };
    }

    public void SetCell(HiveCell cell)
    {
        _cell = cell;
        KeyText.Text = cell.Letter.ToString();

        // Below the icon: window title when running, app name otherwise
        CaptionText.Text = cell.IsRunning ? cell.Title : cell.AppName;
        CaptionText.Foreground = cell.IsRunning
            ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F2FFFFFF"))
            : new SolidColorBrush((Color)ColorConverter.ConvertFromString("#CCF5C542"));
        CaptionText.FontSize = cell.IsRunning ? 11 : 10;

        // Pinned but not running: an extra "click to launch" line under the name
        bool awaitingLaunch = cell.IsPinned && !cell.IsRunning;
        HintText.Visibility = awaitingLaunch ? Visibility.Visible : Visibility.Collapsed;

        if (cell.Icon != null)
        {
            AppIcon.Source = cell.Icon;
            AppIcon.Opacity = 0.9;
            AppIcon.Visibility = Visibility.Visible;
            FallbackGlyph.Visibility = Visibility.Collapsed;
        }
        else
        {
            AppIcon.Visibility = Visibility.Collapsed;
            FallbackGlyph.Text = string.IsNullOrEmpty(cell.AppName) ? "?" : cell.AppName.Substring(0, 1).ToUpperInvariant();
            FallbackGlyph.Visibility = Visibility.Visible;
        }
    }

    private void SetHover(bool hover)
    {
        // Only the scale transforms and the external glow overlay animate; nothing
        // inside the bitmap-cached grid changes, so the cache survives the animation.
        // The text layer lives outside the cache and follows the same transforms via
        // bindings, re-rasterizing glyphs as vectors so text stays sharp while scaling.
        double scale = hover ? 1.06 : 1.0;
        SplineAnimate(HoverScaleTransform, ScaleTransform.ScaleXProperty, scale, 300);
        SplineAnimate(HoverScaleTransform, ScaleTransform.ScaleYProperty, scale, 300);
        SplineAnimate(HoverGlow, OpacityProperty, hover ? 1.0 : 0.0, 300);
    }

    /// <summary>Slide the cell away from / back to the centre cut line while searching.</summary>
    public void SetSearching(bool searching)
    {
        if (_cell == null)
            return;

        var center = KeyGrid.CenterOf(_cell.Letter);
        bool isLeftSide = center.X < KeyGrid.CutX;
        double distFromCut = Math.Abs(center.X - KeyGrid.CutX) / KeyGrid.PitchX;
        double shift = searching ? (isLeftSide ? -1 : 1) * (KeyGrid.PitchX * 2.6) : 0;
        double delay = searching ? distFromCut * 40 : (4 - Math.Min(distFromCut, 4)) * 40;

        SplineAnimate(ShiftTransform, TranslateTransform.XProperty, shift, 420, delay);
        double scale = searching ? 0.92 : 1.0;
        SplineAnimate(SearchScaleTransform, ScaleTransform.ScaleXProperty, scale, 420, delay);
        SplineAnimate(SearchScaleTransform, ScaleTransform.ScaleYProperty, scale, 420, delay);
        SplineAnimate(this, OpacityProperty, searching ? 0.6 : 1.0, 420, delay);
    }

    /// <summary>Clear search transforms instantly, without scheduling animations.</summary>
    public void ResetSearchTransforms()
    {
        ShiftTransform.BeginAnimation(TranslateTransform.XProperty, null);
        SearchScaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        SearchScaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        BeginAnimation(OpacityProperty, null);
        ShiftTransform.X = 0;
        SearchScaleTransform.ScaleX = 1;
        SearchScaleTransform.ScaleY = 1;
        Opacity = 1;
    }

    private static void SplineAnimate(IAnimatable target, DependencyProperty property, double to, double durationMs, double delayMs = 0)
    {
        var animation = new DoubleAnimationUsingKeyFrames
        {
            BeginTime = TimeSpan.FromMilliseconds(delayMs)
        };
        animation.KeyFrames.Add(new SplineDoubleKeyFrame(
            to,
            KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(durationMs)),
            new KeySpline(0.22, 1, 0.36, 1)));
        target.BeginAnimation(property, animation);
    }
}
