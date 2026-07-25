using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;

namespace HiveMotion;

/// <summary>
/// Global low-level keyboard hook driven by a list of <see cref="HotkeyRule"/> combos.
/// Hidden overlay: swallow the combo and ask to open. Open overlay: let the combo through
/// to the system (native function fires) and notify so we close.
/// </summary>
public sealed class GlobalKeyboardHook : IDisposable
{
    private const uint InjectedExtraInfo = 0x484D4F54; // 'HMOT': marks our own synthetic chord key
    private const int VkUnassigned = 0xE8;

    private readonly IReadOnlyList<HotkeyRule> _rules;
    private readonly HashSet<int> _swallowedKeys = new();
    private readonly ManualResetEvent _ready = new(false);
    private Thread? _hookThread;
    private uint _hookThreadId;
    private IntPtr _hookHandle = IntPtr.Zero;
    private NativeMethods.LowLevelKeyboardProc? _hookProc;
    private bool _disposed;

    /// <summary>Mirror of the overlay visibility, written by the app; decides swallow vs pass-through.</summary>
    public bool IsOverlayOpen { get; set; }

    /// <summary>
    /// True: a second press while open reaches the system (native UI fires). False: the
    /// second press is swallowed, the app only closes the overlay.
    /// </summary>
    public bool PassThroughOnSecondPress { get; set; } = true;

    /// <summary>A registered combo fired while the overlay was hidden: open the overlay.</summary>
    public event EventHandler<HotkeyEventArgs>? HotkeyOpenRequested;
    /// <summary>A registered combo fired while the overlay was open: passed to the system, close the overlay.</summary>
    public event EventHandler<HotkeyEventArgs>? HotkeyPassthrough;

    public GlobalKeyboardHook(IReadOnlyList<HotkeyRule> rules)
    {
        _rules = rules;
    }

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
    /// The OS opens Start when Win is released with no chord. We swallow the combo key, so we
    /// inject a harmless unassigned key while Win is still held: the shell sees a chord and
    /// stays quiet, and the Win key state never gets stuck.
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

        // Swallow key-ups of combo keys we swallowed on the way down (no orphan key-ups).
        if (isUp && _swallowedKeys.Contains(vk))
        {
            _swallowedKeys.Remove(vk);
            return (IntPtr)1;
        }
        if (!isDown)
            return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);

        foreach (var rule in _rules)
        {
            if (vk != rule.Vk || !ModifiersMatch(rule))
                continue;

            long receiptTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
            Logger.Info($"hotkey {rule.Name}: overlayOpen={IsOverlayOpen} foreground={DescribeForeground()}");
            var request = new HotkeyEventArgs(rule, receiptTimestamp);

            // The combo's own native UI (Task View, Game Bar…) is foreground: let the key
            // reach it so the native UI toggles itself closed instead of reopening our overlay.
            if (IsForegroundNativeUi(rule))
                return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);

            if (IsOverlayOpen)
            {
                HotkeyPassthrough?.Invoke(this, request);
                if (PassThroughOnSecondPress)
                {
                    // Generic rule: a second press goes to the system (Task View, Game Bar…).
                    return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
                }
                // Close-only mode: swallow the combo so its native UI never fires.
                _swallowedKeys.Add(vk);
                if (rule.Win)
                    InjectBenignChordKey();
                return (IntPtr)1;
            }

            _swallowedKeys.Add(vk);
            HotkeyOpenRequested?.Invoke(this, request);
            if (rule.Win)
                InjectBenignChordKey();
            return (IntPtr)1;
        }

        return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    private static string DescribeForeground()
    {
        try
        {
            IntPtr foreground = NativeMethods.GetForegroundWindow();
            var className = new System.Text.StringBuilder(256);
            NativeMethods.GetClassName(foreground, className, className.Capacity);
            return className.ToString();
        }
        catch
        {
            return "?";
        }
    }

    private static bool IsForegroundNativeUi(HotkeyRule rule)
    {
        if (rule.NativeClassNames.Length == 0 && rule.NativeProcessNames.Length == 0)
            return false;

        IntPtr foreground = NativeMethods.GetForegroundWindow();
        if (foreground == IntPtr.Zero)
            return false;

        if (rule.NativeClassNames.Length > 0)
        {
            var className = new System.Text.StringBuilder(256);
            NativeMethods.GetClassName(foreground, className, className.Capacity);
            foreach (string name in rule.NativeClassNames)
            {
                if (string.Equals(className.ToString(), name, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        if (rule.NativeProcessNames.Length > 0)
        {
            try
            {
                NativeMethods.GetWindowThreadProcessId(foreground, out uint pid);
                string processName = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName;
                foreach (string name in rule.NativeProcessNames)
                {
                    if (string.Equals(processName, name, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            catch
            {
                // process may have exited between the two calls
            }
        }

        return false;
    }

    private static bool ModifiersMatch(HotkeyRule rule)
    {
        bool win = (NativeMethods.GetAsyncKeyState(NativeMethods.VK_LWIN) & 0x8000) != 0 ||
                   (NativeMethods.GetAsyncKeyState(NativeMethods.VK_RWIN) & 0x8000) != 0;
        bool ctrl = (NativeMethods.GetAsyncKeyState(NativeMethods.VK_CONTROL) & 0x8000) != 0;
        bool alt = (NativeMethods.GetAsyncKeyState(NativeMethods.VK_MENU) & 0x8000) != 0;
        bool shift = (NativeMethods.GetAsyncKeyState(NativeMethods.VK_SHIFT) & 0x8000) != 0;

        return rule.Win == win && rule.Ctrl == ctrl && rule.Alt == alt && rule.Shift == shift;
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

public sealed class HotkeyEventArgs : EventArgs
{
    public HotkeyEventArgs(HotkeyRule rule, long receiptTimestamp)
    {
        Rule = rule;
        ReceiptTimestamp = receiptTimestamp;
    }

    public HotkeyRule Rule { get; }
    public long ReceiptTimestamp { get; }
}
