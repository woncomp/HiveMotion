using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;

namespace HiveMotion;

public sealed class GlobalKeyboardHook : IDisposable
{
    private const uint InjectedExtraInfo = 0x484D4F54; // 'HMOT': marks our own synthetic chord key
    private const int VkUnassigned = 0xE8;

    private readonly ManualResetEvent _ready = new(false);
    private Thread? _hookThread;
    private uint _hookThreadId;
    private IntPtr _hookHandle = IntPtr.Zero;
    private NativeMethods.LowLevelKeyboardProc? _hookProc;
    private bool _disposed;

    /// <summary>Overlay is visible: letters / space / esc are consumed by us.</summary>
    public bool OverlayOpen { get; set; }

    /// <summary>Search box is active: typed characters edit the query instead of jumping to cells.</summary>
    public bool Searching { get; set; }

    public event EventHandler? WinTabPressed;
    public event EventHandler? EscapePressed;
    /// <summary>A cell hotkey (A-Z) while the overlay is open and not searching.</summary>
    public event EventHandler<char>? CellKeyPressed;
    /// <summary>Space pressed while the overlay is open and not searching: enter search mode.</summary>
    public event EventHandler? SearchRequested;
    /// <summary>A character typed while searching.</summary>
    public event EventHandler<char>? SearchCharTyped;
    public event EventHandler? SearchBackspace;
    public event EventHandler? SearchSubmit;
    /// <summary>Arrow key while searching: -1 = up, +1 = down.</summary>
    public event EventHandler<int>? SearchArrow;

    public void Start()
    {
        if (_hookThread != null)
            return;

        _hookThread = new Thread(HookThreadProc)
        {
            IsBackground = true,
            Name = "HiveMotionKeyboardHook"
        };
        _hookThread.SetApartmentState(ApartmentState.STA);
        _hookThread.Start();
        _ready.WaitOne();
    }

    public void Stop()
    {
        if (_hookHandle != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_hookHandle);
            _hookHandle = IntPtr.Zero;
        }

        if (_hookThread != null && _hookThread.IsAlive && _hookThreadId != 0)
        {
            NativeMethods.PostThreadMessage(_hookThreadId, NativeMethods.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            _hookThread.Join(TimeSpan.FromSeconds(2));
            _hookThread = null;
        }
    }

    private void HookThreadProc()
    {
        _hookThreadId = NativeMethods.GetCurrentThreadId();
        _hookProc = new NativeMethods.LowLevelKeyboardProc(HookCallback);
        _hookHandle = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, _hookProc, IntPtr.Zero, 0);

        if (_hookHandle == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error());

        _ready.Set();

        NativeMethods.MSG msg;
        while (NativeMethods.GetMessage(out msg, IntPtr.Zero, 0, 0) > 0)
        {
            NativeMethods.TranslateMessage(ref msg);
            NativeMethods.DispatchMessage(ref msg);
        }
    }

    /// <summary>
    /// The OS opens Start when Win is released with no chord. We swallow Tab, so we inject a
    /// harmless unassigned key while Win is still held: the shell sees a chord and stays quiet,
    /// and the Win key state never gets stuck.
    /// </summary>
    private static void InjectBenignChordKey()
    {
        var inputs = new[]
        {
            new NativeMethods.INPUT
            {
                type = NativeMethods.INPUT_KEYBOARD,
                u = new NativeMethods.KEYBDINPUT { wVk = VkUnassigned, dwExtraInfo = (IntPtr)InjectedExtraInfo }
            },
            new NativeMethods.INPUT
            {
                type = NativeMethods.INPUT_KEYBOARD,
                u = new NativeMethods.KEYBDINPUT { wVk = VkUnassigned, dwFlags = NativeMethods.KEYEVENTF_KEYUP, dwExtraInfo = (IntPtr)InjectedExtraInfo }
            }
        };
        NativeMethods.SendInput(2, inputs, Marshal.SizeOf<NativeMethods.INPUT>());
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0)
            return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);

        int msg = (int)wParam;
        bool isDown = msg == NativeMethods.WM_KEYDOWN || msg == NativeMethods.WM_SYSKEYDOWN;
        bool isUp = msg == NativeMethods.WM_KEYUP || msg == NativeMethods.WM_SYSKEYUP;
        if (!isDown && !isUp)
            return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);

        var kbd = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
        if (kbd.dwExtraInfo == (IntPtr)InjectedExtraInfo)
            return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);

        int vk = (int)kbd.vkCode;

        bool winDown = (NativeMethods.GetAsyncKeyState(NativeMethods.VK_LWIN) & 0x8000) != 0 ||
                       (NativeMethods.GetAsyncKeyState(NativeMethods.VK_RWIN) & 0x8000) != 0;

        if (vk == NativeMethods.VK_TAB && winDown)
        {
            if (isDown)
            {
                WinTabPressed?.Invoke(this, EventArgs.Empty);
                InjectBenignChordKey();
            }
            return (IntPtr)1;
        }

        if (!OverlayOpen)
            return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);

        // Key-ups for keys we swallow must be swallowed too, or apps see orphan key-ups.
        bool handled;
        if (vk == NativeMethods.VK_ESCAPE)
        {
            handled = true;
            if (isDown)
                EscapePressed?.Invoke(this, EventArgs.Empty);
        }
        else if (Searching)
        {
            handled = true;
            switch (vk)
            {
                case >= NativeMethods.VK_A and <= NativeMethods.VK_Z:
                    if (isDown) SearchCharTyped?.Invoke(this, (char)('a' + vk - NativeMethods.VK_A));
                    break;
                case >= NativeMethods.VK_0 and <= NativeMethods.VK_9:
                    if (isDown) SearchCharTyped?.Invoke(this, (char)('0' + vk - NativeMethods.VK_0));
                    break;
                case >= NativeMethods.VK_NUMPAD0 and <= NativeMethods.VK_NUMPAD9:
                    if (isDown) SearchCharTyped?.Invoke(this, (char)('0' + vk - NativeMethods.VK_NUMPAD0));
                    break;
                case NativeMethods.VK_SPACE:
                    if (isDown) SearchCharTyped?.Invoke(this, ' ');
                    break;
                case NativeMethods.VK_OEM_MINUS:
                    if (isDown) SearchCharTyped?.Invoke(this, '-');
                    break;
                case NativeMethods.VK_OEM_PERIOD:
                    if (isDown) SearchCharTyped?.Invoke(this, '.');
                    break;
                case NativeMethods.VK_BACK:
                    if (isDown) SearchBackspace?.Invoke(this, EventArgs.Empty);
                    break;
                case NativeMethods.VK_RETURN:
                    if (isDown) SearchSubmit?.Invoke(this, EventArgs.Empty);
                    break;
                case NativeMethods.VK_UP:
                    if (isDown) SearchArrow?.Invoke(this, -1);
                    break;
                case NativeMethods.VK_DOWN:
                    if (isDown) SearchArrow?.Invoke(this, +1);
                    break;
                default:
                    handled = false;
                    break;
            }
        }
        else if (vk is >= NativeMethods.VK_A and <= NativeMethods.VK_Z)
        {
            handled = true;
            if (isDown)
                CellKeyPressed?.Invoke(this, (char)('A' + vk - NativeMethods.VK_A));
        }
        else if (vk == NativeMethods.VK_SPACE)
        {
            handled = true;
            if (isDown)
                SearchRequested?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            handled = false;
        }

        return handled
            ? (IntPtr)1
            : NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        Stop();
        _ready.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
