using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace HiveMotion;

public partial class OverlayWindow : Window
{
    private readonly MainMenuView _mainMenuView = new();
    private readonly SubMenuView _subMenuView = new();
    private readonly AppItem _appItem;

    public OverlayWindow(AppItem appItem)
    {
        _appItem = appItem ?? throw new ArgumentNullException(nameof(appItem));
        InitializeComponent();
        _mainMenuView.DataContext = appItem;
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Remove WS_EX_APPWINDOW to hide from Alt+Tab; keep tool window style.
        var helper = new WindowInteropHelper(this);
        int exStyle = NativeMethods.GetWindowLong(helper.Handle, NativeMethods.GWL_EXSTYLE);
        exStyle = (exStyle | NativeMethods.WS_EX_TOOLWINDOW) & ~NativeMethods.WS_EX_APPWINDOW;
        NativeMethods.SetWindowLong(helper.Handle, NativeMethods.GWL_EXSTYLE, exStyle);
    }

    public void ShowMainMenu()
    {
        Dispatcher.BeginInvoke(() =>
        {
            ViewHost.Content = _mainMenuView;
            Show();
            Opacity = 1;
        });
    }

    public void ShowSubMenu(IEnumerable<WindowItem> windows)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _subMenuView.SetItems(windows);
            ViewHost.Content = _subMenuView;
            _subMenuView.PlayAnimation();
        });
    }

    public void HideOverlay()
    {
        Dispatcher.BeginInvoke(() =>
        {
            var storyboard = new Storyboard();
            var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(150));
            Storyboard.SetTarget(fade, this);
            Storyboard.SetTargetProperty(fade, new PropertyPath(OpacityProperty));
            storyboard.Children.Add(fade);
            storyboard.Completed += (_, _) =>
            {
                ViewHost.Content = null;
                Hide();
            };
            storyboard.Begin();
        });
    }

    public new void Hide()
    {
        Dispatcher.BeginInvoke(() =>
        {
            ViewHost.Content = null;
            base.Hide();
            Opacity = 1;
        });
    }

    public void ShowOverlay()
    {
        Dispatcher.BeginInvoke(() =>
        {
            Show();
            Opacity = 1;
        });
    }
}
