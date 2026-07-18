using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace HiveMotion;

public partial class HiveCellView : System.Windows.Controls.UserControl
{
    private HiveCell? _cell;

    public event EventHandler<HiveCell>? Clicked;

    public HiveCellView()
    {
        InitializeComponent();
        MouseEnter += (_, _) => SetHover(true);
        MouseLeave += (_, _) => SetHover(false);
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
        TitleText.Text = cell.Title;
        AppNameText.Text = cell.IsRunning ? cell.AppName : "点 击 启 动";

        if (cell.Icon != null)
        {
            AppIcon.Source = cell.Icon;
            AppIcon.Opacity = cell.IsRunning ? 0.9 : 0.55;
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
        double scale = hover ? 1.06 : 1.0;
        SplineAnimate(HoverScaleTransform, ScaleTransform.ScaleXProperty, scale, 300);
        SplineAnimate(HoverScaleTransform, ScaleTransform.ScaleYProperty, scale, 300);
        SplineAnimate(IconScale, ScaleTransform.ScaleXProperty, hover ? 1.1 : 1.0, 300);
        SplineAnimate(IconScale, ScaleTransform.ScaleYProperty, hover ? 1.1 : 1.0, 300);
        SplineAnimate(KeycapScale, ScaleTransform.ScaleXProperty, hover ? 1.1 : 1.0, 300);
        SplineAnimate(KeycapScale, ScaleTransform.ScaleYProperty, hover ? 1.1 : 1.0, 300);
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
