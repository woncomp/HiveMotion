using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;

namespace HiveMotion;

public sealed class GlobalKeyboardHook : IDisposable
{
    private readonly ManualResetEvent _ready = new(false);
    private Thread? _hookThread;
    private uint _hookThreadId;
    private IntPtr _hookHandle = IntPtr.Zero;
    private NativeMethods.LowLevelKeyboardProc? _hookProc;
    private bool _disposed;

    public bool ShouldInterceptWinTab { get; set; } = true;
    public bool ShouldInterceptAppKey { get; set; } = false;
    public bool ShouldInterceptNumberKeys { get; set; } = false;

    public event EventHandler? WinTabPressed;
    public event EventHandler? EscapePressed;
    public event EventHandler<char>? AppKeyPressed;
    public event EventHandler<int>? NumberKeyPressed;

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

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0)
            return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);

        int msg = (int)wParam;
        if (msg != NativeMethods.WM_KEYDOWN && msg != NativeMethods.WM_SYSKEYDOWN)
            return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);

        var kbd = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
        int vk = (int)kbd.vkCode;

        bool winDown = (NativeMethods.GetAsyncKeyState(NativeMethods.VK_LWIN) & 0x8000) != 0 ||
                       (NativeMethods.GetAsyncKeyState(NativeMethods.VK_RWIN) & 0x8000) != 0;

        if (vk == NativeMethods.VK_TAB && winDown)
        {
            bool intercept = ShouldInterceptWinTab;
            WinTabPressed?.Invoke(this, EventArgs.Empty);
            return intercept ? (IntPtr)1 : NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
        }

        if (vk == NativeMethods.VK_ESCAPE)
        {
            EscapePressed?.Invoke(this, EventArgs.Empty);
            return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
        }

        if (vk == NativeMethods.VK_N)
        {
            bool intercept = ShouldInterceptAppKey;
            AppKeyPressed?.Invoke(this, 'N');
            return intercept ? (IntPtr)1 : NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
        }

        int? number = vk switch
        {
            >= NativeMethods.VK_0 and <= NativeMethods.VK_9 => vk - NativeMethods.VK_0,
            >= NativeMethods.VK_NUMPAD0 and <= NativeMethods.VK_NUMPAD9 => vk - NativeMethods.VK_NUMPAD0,
            _ => null
        };

        if (number.HasValue)
        {
            bool intercept = ShouldInterceptNumberKeys;
            NumberKeyPressed?.Invoke(this, number.Value);
            return intercept ? (IntPtr)1 : NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
        }

        return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
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
