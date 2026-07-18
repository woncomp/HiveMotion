using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using Cursors = System.Windows.Input.Cursors;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Image = System.Windows.Controls.Image;
using Orientation = System.Windows.Controls.Orientation;
using Point = System.Windows.Point;

namespace HiveMotion;

public partial class TaskGridView : System.Windows.Controls.UserControl
{
    private readonly List<HiveCellView> _cellViews = new();
    private readonly List<(Border Root, Border Bar, HiveCell Cell)> _itemVisuals = new();
    private IReadOnlyList<HiveCell> _cells = Array.Empty<HiveCell>();
    private List<HiveCell> _results = new();
    private string _query = string.Empty;
    private int _highlight;
    private bool _searching;
    private bool _previewVisible;
    private double _previewMaxH = 320;
    private readonly DwmThumbnailPreview _dwmPreview = new();

    /// <summary>HWND of the owning overlay window; the DWM thumbnail draws into it.</summary>
    public IntPtr OverlayHwnd { get; set; }

    public event EventHandler<HiveCell>? CellChosen;
    public event EventHandler? CloseRequested;

    public TaskGridView()
    {
        InitializeComponent();
    }

    public bool Searching => _searching;

    public void SetBackdrop(System.Windows.Media.ImageSource? backdrop)
    {
        BackdropImage.Source = backdrop;
    }

    public void SetCells(IReadOnlyList<HiveCell> cells)
    {
        _cells = cells;
        HexCanvas.Children.Clear();
        _cellViews.Clear();

        foreach (var cell in cells)
        {
            var view = new HiveCellView();
            view.SetCell(cell);
            view.Clicked += (_, chosen) => CellChosen?.Invoke(this, chosen);
            view.Hovered += (_, hovered) => ShowPreview(hovered);
            view.Unhovered += (_, _) => HidePreview();
            var center = KeyGrid.CenterOf(cell.Letter);
            Canvas.SetLeft(view, center.X - KeyGrid.HexW / 2);
            Canvas.SetTop(view, center.Y - KeyGrid.HexH / 2);
            HexCanvas.Children.Add(view);
            _cellViews.Add(view);
        }

        _previewVisible = false;
        _dwmPreview.Hide();
        HoverPreview.BeginAnimation(UIElement.OpacityProperty, null);
        HoverPreview.Opacity = 0;

        ExitSearchImmediate();
    }

    public void EnterSearch()
    {
        if (_searching)
            return;
        _searching = true;
        HidePreview();
        _query = string.Empty;
        QueryText.Text = string.Empty;
        QueryPlaceholder.Visibility = Visibility.Visible;

        BarIdle.Visibility = Visibility.Collapsed;
        BarSearch.Visibility = Visibility.Visible;
        SpaceBarBorderBrush.Color = (Color)ColorConverter.ConvertFromString("#A6F5B301");
        SpaceBarRidge.Opacity = 1;
        EscHintText.Text = "退 出 搜 索";

        RebuildResults();
        ResultPanel.Visibility = Visibility.Visible;
        SplineAnimate(ResultPanel, UIElement.OpacityProperty, 1, 400);
        SplineAnimate(ResultPanelSlide, TranslateTransform.YProperty, 0, 400);

        foreach (var view in _cellViews)
            view.SetSearching(true);
    }

    public void ExitSearch()
    {
        if (!_searching)
            return;
        _searching = false;

        BarSearch.Visibility = Visibility.Collapsed;
        BarIdle.Visibility = Visibility.Visible;
        SpaceBarBorderBrush.Color = (Color)ColorConverter.ConvertFromString("#33FFFFFF");
        SpaceBarRidge.Opacity = 0.6;
        EscHintText.Text = "关 闭";

        SplineAnimate(ResultPanel, UIElement.OpacityProperty, 0, 300);
        SplineAnimate(ResultPanelSlide, TranslateTransform.YProperty, 24, 300);
        var panel = ResultPanel;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!_searching)
                panel.Visibility = Visibility.Collapsed;
        }), System.Windows.Threading.DispatcherPriority.Background);

        foreach (var view in _cellViews)
            view.SetSearching(false);
    }

    private void ExitSearchImmediate()
    {
        _searching = false;
        _query = string.Empty;
        QueryText.Text = string.Empty;
        BarSearch.Visibility = Visibility.Collapsed;
        BarIdle.Visibility = Visibility.Visible;
        SpaceBarBorderBrush.Color = (Color)ColorConverter.ConvertFromString("#33FFFFFF");
        SpaceBarRidge.Opacity = 0.6;
        EscHintText.Text = "关 闭";
        ResultPanel.BeginAnimation(UIElement.OpacityProperty, null);
        ResultPanel.Opacity = 0;
        ResultPanel.Visibility = Visibility.Collapsed;
        foreach (var view in _cellViews)
            view.ResetSearchTransforms();
    }

    public void AppendSearchChar(char c)
    {
        if (!_searching)
            return;
        _query += c;
        QueryText.Text = _query;
        QueryPlaceholder.Visibility = Visibility.Collapsed;
        RebuildResults();
    }

    public void SearchBackspace()
    {
        if (!_searching || _query.Length == 0)
            return;
        _query = _query.Substring(0, _query.Length - 1);
        QueryText.Text = _query;
        QueryPlaceholder.Visibility = _query.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        RebuildResults();
    }

    public void MoveSearchHighlight(int delta)
    {
        if (!_searching || _results.Count == 0)
            return;
        SetHighlight(Math.Clamp(_highlight + delta, 0, _results.Count - 1));
    }

    public void SubmitSearch()
    {
        if (!_searching || _results.Count == 0)
            return;
        CellChosen?.Invoke(this, _results[Math.Clamp(_highlight, 0, _results.Count - 1)]);
    }

    private void ShowPreview(HiveCell cell)
    {
        if (_searching || !cell.IsRunning || OverlayHwnd == IntPtr.Zero)
        {
            HidePreview();
            return;
        }

        var (contentW, contentH) = GetWindowContentSize(cell.WindowHandle);
        if (contentW <= 0)
        {
            HidePreview();
            return;
        }

        double availW = Root.ActualWidth * 0.45;
        double availH = _previewMaxH;
        double scale = Math.Min(availW / contentW, availH / contentH);
        HoverPreviewViewport.Width = contentW * scale;
        HoverPreviewViewport.Height = contentH * scale;

        bool wasVisible = _previewVisible;
        _previewVisible = true;
        if (!wasVisible)
            SplineAnimate(HoverPreview, UIElement.OpacityProperty, 1, 160);

        // Attach the thumbnail once the frame's new size has been arranged.
        var handle = cell.WindowHandle;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
            new Action(() => AttachThumbnail(handle)));
    }

    private void AttachThumbnail(IntPtr sourceHwnd)
    {
        if (!_previewVisible || OverlayHwnd == IntPtr.Zero)
            return;

        var dpi = VisualTreeHelper.GetDpi(this);
        var topLeft = HoverPreviewViewport.PointToScreen(new Point(0, 0));
        var rect = new NativeMethods.RECT
        {
            Left = (int)Math.Round(topLeft.X),
            Top = (int)Math.Round(topLeft.Y),
            Right = (int)Math.Round(topLeft.X + HoverPreviewViewport.ActualWidth * dpi.DpiScaleX),
            Bottom = (int)Math.Round(topLeft.Y + HoverPreviewViewport.ActualHeight * dpi.DpiScaleY)
        };
        _dwmPreview.Show(OverlayHwnd, sourceHwnd, rect);
    }

    private void HidePreview()
    {
        _dwmPreview.Hide();
        if (!_previewVisible)
            return;
        _previewVisible = false;
        SplineAnimate(HoverPreview, UIElement.OpacityProperty, 0, 140);
    }

    public void ResetPreview() => HidePreview();

    /// <summary>Aspect source for fitting the preview: restore rect for minimized windows, client rect otherwise.</summary>
    private static (double w, double h) GetWindowContentSize(IntPtr hwnd)
    {
        if (NativeMethods.IsIconic(hwnd))
        {
            var placement = new NativeMethods.WINDOWPLACEMENT { length = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.WINDOWPLACEMENT>() };
            if (NativeMethods.GetWindowPlacement(hwnd, ref placement))
            {
                double w = placement.rcNormalPosition.Right - placement.rcNormalPosition.Left;
                double h = placement.rcNormalPosition.Bottom - placement.rcNormalPosition.Top;
                if (w > 40 && h > 40)
                    return (w, h);
            }
        }

        if (NativeMethods.GetClientRect(hwnd, out var client))
        {
            double w = client.Right - client.Left;
            double h = client.Bottom - client.Top;
            if (w > 40 && h > 40)
                return (w, h);
        }
        return (0, 0);
    }

    private void RebuildResults()
    {
        _results = _cells
            .Where(MatchesQuery)
            .OrderBy(c => c.IsRunning ? 0 : 1)
            .ThenBy(c => c.Letter)
            .ToList();

        ResultList.Children.Clear();
        _itemVisuals.Clear();

        for (int i = 0; i < _results.Count; i++)
        {
            var visual = BuildResultItem(_results[i], i);
            ResultList.Children.Add(visual.Root);
            _itemVisuals.Add(visual);
        }

        ResultHeaderText.Text = $"全 部 窗 口 · {_results.Count}";
        ResultEmpty.Visibility = _results.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ResultHeader.Visibility = _results.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        SetHighlight(0);
    }

    private bool MatchesQuery(HiveCell cell)
    {
        if (string.IsNullOrWhiteSpace(_query))
            return true;
        string q = _query.Trim();
        return cell.Title.Contains(q, StringComparison.OrdinalIgnoreCase)
            || cell.AppName.Contains(q, StringComparison.OrdinalIgnoreCase)
            || (q.Length == 1 && char.ToUpperInvariant(q[0]) == cell.Letter);
    }

    private (Border Root, Border Bar, HiveCell Cell) BuildResultItem(HiveCell cell, int index)
    {
        var bar = new Border
        {
            Width = 3,
            Height = 24,
            CornerRadius = new CornerRadius(1.5),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(-9, 0, 0, 0),
            Visibility = Visibility.Hidden,
            Background = new LinearGradientBrush(
                (Color)ColorConverter.ConvertFromString("#FFE6A3"),
                (Color)ColorConverter.ConvertFromString("#F5B301"), 90)
        };

        FrameworkElement iconContent;
        if (cell.Icon != null)
        {
            var iconImage = new Image
            {
                Source = cell.Icon,
                Width = 24,
                Height = 24,
                Opacity = cell.IsRunning ? 1 : 0.55
            };
            RenderOptions.SetBitmapScalingMode(iconImage, BitmapScalingMode.HighQuality);
            iconContent = iconImage;
        }
        else
        {
            iconContent = new TextBlock
            {
                Text = string.IsNullOrEmpty(cell.AppName) ? "?" : cell.AppName.Substring(0, 1).ToUpperInvariant(),
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#99FFFFFF")),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        var iconBorder = new Border
        {
            Width = 40,
            Height = 32,
            CornerRadius = new CornerRadius(4),
            BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#26FFFFFF")),
            BorderThickness = new Thickness(1),
            ClipToBounds = true,
            Child = new Grid { Children = { iconContent } },
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        var title = new TextBlock
        {
            Text = cell.Title,
            FontSize = 13,
            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#E6FFFFFF")),
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        var subtitle = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
        subtitle.Children.Add(new TextBlock
        {
            Text = cell.AppName,
            FontSize = 11,
            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#66FFFFFF"))
        });
        if (cell.IsRunning)
        {
            subtitle.Children.Add(new Border
            {
                Width = 6,
                Height = 6,
                CornerRadius = new CornerRadius(3),
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F5B301")),
                Margin = new Thickness(8, 0, 4, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
            subtitle.Children.Add(new TextBlock
            {
                Text = "运 行 中",
                FontSize = 11,
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#D9FFD97A")),
                VerticalAlignment = VerticalAlignment.Center
            });
        }
        else
        {
            subtitle.Children.Add(new TextBlock
            {
                Text = "· 未 运 行 , 选 择 以 启 动",
                FontSize = 11,
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#4DFFFFFF")),
                Margin = new Thickness(6, 0, 0, 0)
            });
        }

        var texts = new StackPanel { Margin = new Thickness(12, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(title);
        texts.Children.Add(subtitle);

        var keycap = new Border
        {
            Width = 24,
            Height = 24,
            CornerRadius = new CornerRadius(6),
            BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#99FFE9B0")),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
            Background = new LinearGradientBrush
            {
                StartPoint = new Point(0.4, 0),
                EndPoint = new Point(0.6, 1),
                GradientStops = new GradientStopCollection
                {
                    new GradientStop((Color)ColorConverter.ConvertFromString("#FFE6A3"), 0),
                    new GradientStop((Color)ColorConverter.ConvertFromString("#F5B301"), 0.6),
                    new GradientStop((Color)ColorConverter.ConvertFromString("#D99300"), 1)
                }
            },
            Child = new TextBlock
            {
                Text = cell.Letter.ToString(),
                FontSize = 12,
                FontWeight = FontWeights.ExtraBold,
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#3D2C00")),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(iconBorder, 0);
        Grid.SetColumn(texts, 1);
        Grid.SetColumn(keycap, 2);
        grid.Children.Add(bar);
        grid.Children.Add(iconBorder);
        grid.Children.Add(texts);
        grid.Children.Add(keycap);

        var root = new Border
        {
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(12, 8, 12, 8),
            Margin = new Thickness(2),
            Cursor = Cursors.Hand,
            Child = grid
        };

        root.MouseEnter += (_, _) => SetHighlight(index);
        root.MouseLeftButtonUp += (_, e) =>
        {
            CellChosen?.Invoke(this, cell);
            e.Handled = true;
        };

        return (root, bar, cell);
    }

    private void SetHighlight(int index)
    {
        if (_results.Count == 0)
        {
            _highlight = 0;
            return;
        }
        index = Math.Clamp(index, 0, _results.Count - 1);
        _highlight = index;

        for (int i = 0; i < _itemVisuals.Count; i++)
        {
            bool active = i == index;
            _itemVisuals[i].Root.Background = active
                ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1FF5B301"))
                : Brushes.Transparent;
            _itemVisuals[i].Bar.Visibility = active ? Visibility.Visible : Visibility.Hidden;
        }
        _itemVisuals[index].Root.BringIntoView();
    }

    private void OnRootSizeChanged(object sender, SizeChangedEventArgs e)
    {
        double w = Root.ActualWidth;
        double h = Root.ActualHeight;
        HiveBox.MaxWidth = w * 0.92;
        HiveBox.MaxHeight = h * 0.52;

        // Keep the hover preview inside the empty band above the hive, never over the cells.
        double gridScale = Math.Min(1, Math.Min(w * 0.92 / 1470, h * 0.52 / 460));
        double gridAreaHeight = 460 * gridScale + 28 + 48;
        double topSpace = (h - gridAreaHeight) / 2;
        _previewMaxH = Math.Clamp(topSpace - 48, 160, 320);
        HoverPreview.MaxWidth = w * 0.45;
    }

    private void OnBackdropClick(object sender, MouseButtonEventArgs e)
    {
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnSpaceBarClick(object sender, MouseButtonEventArgs e)
    {
        if (!_searching)
        {
            EnterSearch();
            e.Handled = true;
        }
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
