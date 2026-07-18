using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace HiveMotion;

public partial class SubMenuView : System.Windows.Controls.UserControl
{
    public SubMenuView()
    {
        InitializeComponent();
    }

    public void SetItems(IEnumerable<WindowItem> items)
    {
        ItemsHost.ItemsSource = items;
    }

    public void PlayAnimation()
    {
        Dispatcher.BeginInvoke(new Action(AnimateItems), System.Windows.Threading.DispatcherPriority.Render);
    }

    private void AnimateItems()
    {
        var containers = GetItemContainers();
        int count = containers.Count;
        if (count == 0)
            return;

        ItemsCanvas.UpdateLayout();
        double canvasWidth = ItemsCanvas.ActualWidth;
        double canvasHeight = ItemsCanvas.ActualHeight;
        double centerX = canvasWidth / 2.0;
        double centerY = canvasHeight / 2.0;
        double itemHeight = 60;
        double spacing = itemHeight + 16;

        for (int i = 0; i < count; i++)
        {
            var border = containers[i];
            border.UpdateLayout();

            double itemWidth = border.ActualWidth > 0 ? border.ActualWidth : 300;
            double targetX = centerX - itemWidth / 2.0;
            double targetY = centerY + (i - (count - 1) / 2.0) * spacing;

            // Initially all items are centered
            Canvas.SetLeft(border, centerX - itemWidth / 2.0);
            Canvas.SetTop(border, centerY - border.ActualHeight / 2.0);

            var transformGroup = (TransformGroup)border.RenderTransform;
            var scale = (ScaleTransform)transformGroup.Children[0];
            scale.ScaleX = 0;
            scale.ScaleY = 0;
            border.Opacity = 0;

            var storyboard = new Storyboard();

            double delay = i * 60;

            var opacityAnim = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(300))
            {
                BeginTime = TimeSpan.FromMilliseconds(delay),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(opacityAnim, border);
            Storyboard.SetTargetProperty(opacityAnim, new PropertyPath(OpacityProperty));
            storyboard.Children.Add(opacityAnim);

            var scaleXAnim = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(350))
            {
                BeginTime = TimeSpan.FromMilliseconds(delay),
                EasingFunction = new BackEase { Amplitude = 0.5, EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(scaleXAnim, border);
            Storyboard.SetTargetProperty(scaleXAnim, new PropertyPath("RenderTransform.Children[0].ScaleX"));
            storyboard.Children.Add(scaleXAnim);

            var scaleYAnim = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(350))
            {
                BeginTime = TimeSpan.FromMilliseconds(delay),
                EasingFunction = new BackEase { Amplitude = 0.5, EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(scaleYAnim, border);
            Storyboard.SetTargetProperty(scaleYAnim, new PropertyPath("RenderTransform.Children[0].ScaleY"));
            storyboard.Children.Add(scaleYAnim);

            var leftAnim = new DoubleAnimation(centerX - itemWidth / 2.0, targetX, TimeSpan.FromMilliseconds(400))
            {
                BeginTime = TimeSpan.FromMilliseconds(delay),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(leftAnim, border);
            Storyboard.SetTargetProperty(leftAnim, new PropertyPath(Canvas.LeftProperty));
            storyboard.Children.Add(leftAnim);

            var topAnim = new DoubleAnimation(centerY - border.ActualHeight / 2.0, targetY, TimeSpan.FromMilliseconds(400))
            {
                BeginTime = TimeSpan.FromMilliseconds(delay),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(topAnim, border);
            Storyboard.SetTargetProperty(topAnim, new PropertyPath(Canvas.TopProperty));
            storyboard.Children.Add(topAnim);

            storyboard.Begin();
        }
    }

    private List<Border> GetItemContainers()
    {
        var result = new List<Border>();
        int count = ItemsHost.Items.Count;
        for (int i = 0; i < count; i++)
        {
            if (ItemsHost.ItemContainerGenerator.ContainerFromIndex(i) is ContentPresenter presenter)
            {
                FindChildBorder(presenter, result);
            }
        }
        return result;
    }

    private static void FindChildBorder(DependencyObject parent, List<Border> result)
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is Border border)
            {
                result.Add(border);
            }
            else
            {
                FindChildBorder(child, result);
            }
        }
    }
}
