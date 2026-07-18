using System;
using System.Collections.Generic;
using System.Windows;

namespace HiveMotion;

public enum OverlayState
{
    Hidden,
    MainMenu,
    SubMenu
}

public partial class App : System.Windows.Application
{
    private const string MutexName = "HiveMotion_SingleInstance";
    private System.Threading.Mutex? _singleInstanceMutex;
    private bool _ownsMutex;

    private GlobalKeyboardHook? _keyboardHook;
    private OverlayWindow? _overlayWindow;
    private WindowManager? _windowManager;
    private TrayIconManager? _trayIconManager;
    private AutoStartManager? _autoStartManager;

    private OverlayState _state = OverlayState.Hidden;
    private IReadOnlyList<WindowItem> _currentSubMenuItems = new List<WindowItem>();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstanceMutex = new System.Threading.Mutex(true, MutexName, out _ownsMutex);
        if (!_ownsMutex)
        {
            Shutdown();
            return;
        }

        var appItem = new AppItem
        {
            Key = 'N',
            ProcessName = "notepad",
            ExecutablePath = "notepad.exe",
            DisplayName = "记事本",
            IconGlyph = "📝"
        };

        _autoStartManager = new AutoStartManager();
        _windowManager = new WindowManager(appItem);
        _overlayWindow = new OverlayWindow(appItem);
        _overlayWindow.Hide();

        _trayIconManager = new TrayIconManager(_autoStartManager);
        _trayIconManager.ExitRequested += (_, _) => Shutdown();

        _keyboardHook = new GlobalKeyboardHook();
        _keyboardHook.WinTabPressed += OnWinTabPressed;
        _keyboardHook.EscapePressed += OnEscapePressed;
        _keyboardHook.AppKeyPressed += OnAppKeyPressed;
        _keyboardHook.NumberKeyPressed += OnNumberKeyPressed;
        _keyboardHook.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _keyboardHook?.Dispose();
        _trayIconManager?.Dispose();
        _overlayWindow?.Close();
        if (_ownsMutex)
        {
            _singleInstanceMutex?.ReleaseMutex();
        }
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }

    private void OnWinTabPressed(object? sender, EventArgs e)
    {
        switch (_state)
        {
            case OverlayState.Hidden:
                _state = OverlayState.MainMenu;
                _keyboardHook!.ShouldInterceptWinTab = false;
                _keyboardHook.ShouldInterceptAppKey = true;
                _keyboardHook.ShouldInterceptNumberKeys = false;
                _overlayWindow!.ShowMainMenu();
                break;

            case OverlayState.MainMenu:
            case OverlayState.SubMenu:
                HideOverlay();
                break;
        }
    }

    private void OnEscapePressed(object? sender, EventArgs e)
    {
        switch (_state)
        {
            case OverlayState.MainMenu:
                HideOverlay();
                break;

            case OverlayState.SubMenu:
                _state = OverlayState.MainMenu;
                _keyboardHook!.ShouldInterceptAppKey = true;
                _keyboardHook.ShouldInterceptNumberKeys = false;
                _overlayWindow!.ShowMainMenu();
                break;
        }
    }

    private void OnAppKeyPressed(object? sender, char key)
    {
        if (_state != OverlayState.MainMenu || key != 'N')
            return;

        var windows = _windowManager!.FindWindows();

        if (windows.Count == 0)
        {
            _windowManager.Launch();
            HideOverlay();
        }
        else if (windows.Count == 1)
        {
            _windowManager.ActivateWindow(windows[0].Handle);
            HideOverlay();
        }
        else
        {
            _currentSubMenuItems = windows;
            _state = OverlayState.SubMenu;
            _keyboardHook!.ShouldInterceptAppKey = false;
            _keyboardHook.ShouldInterceptNumberKeys = true;
            _overlayWindow!.ShowSubMenu(windows);
        }
    }

    private void OnNumberKeyPressed(object? sender, int number)
    {
        if (_state != OverlayState.SubMenu)
            return;

        foreach (var item in _currentSubMenuItems)
        {
            if (item.Index == number)
            {
                _windowManager!.ActivateWindow(item.Handle);
                HideOverlay();
                return;
            }
        }
    }

    private void HideOverlay()
    {
        _state = OverlayState.Hidden;
        _keyboardHook!.ShouldInterceptWinTab = true;
        _keyboardHook.ShouldInterceptAppKey = false;
        _keyboardHook.ShouldInterceptNumberKeys = false;
        _overlayWindow!.HideOverlay();
        _currentSubMenuItems = new List<WindowItem>();
    }
}
