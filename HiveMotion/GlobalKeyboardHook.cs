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
    private const int VkUnassigned = 0xE8;

    private readonly IReadOnlyList<HotkeyRule> _rules;
    private readonly Dictionary<int, string> _swallowedKeys = new();
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

        Logger.ActivationInfo($"Starting global keyboard hook with {_rules.Count} configured rules.");
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
        Logger.ActivationInfo("Stopping global keyboard hook.");
        if (_hookHandle != IntPtr.Zero)
        {
            bool removed = NativeMethods.UnhookWindowsHookEx(_hookHandle);
            Logger.ActivationInfo($"UnhookWindowsHookEx completed; success={removed}.");
            _hookHandle = IntPtr.Zero;
        }

        if (_hookThread != null && _hookThread.IsAlive && _hookThreadId != 0)
        {
            NativeMethods.PostThreadMessage(_hookThreadId, NativeMethods.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            bool joined = _hookThread.Join(TimeSpan.FromSeconds(2));
            Logger.ActivationInfo($"Keyboard hook thread stop request completed; joined={joined}.");
            _hookThread = null;
        }
    }

    private void HookThreadProc()
    {
        _hookThreadId = NativeMethods.GetCurrentThreadId();
        Logger.ActivationInfo($"Keyboard hook thread started; threadId={_hookThreadId}.");
        _hookProc = new NativeMethods.LowLevelKeyboardProc(HookCallback);
        _hookHandle = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, _hookProc, IntPtr.Zero, 0);

        if (_hookHandle == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error());

        Logger.ActivationInfo($"Keyboard hook installed; handle={FormatHandle(_hookHandle)}.");
        _ready.Set();

        NativeMethods.MSG msg;
        while (NativeMethods.GetMessage(out msg, IntPtr.Zero, 0, 0) > 0)
        {
            NativeMethods.TranslateMessage(ref msg);
            NativeMethods.DispatchMessage(ref msg);
        }
        Logger.ActivationInfo("Keyboard hook message loop exited.");
    }

    /// <summary>
    /// The OS opens Start when Win is released with no chord. We swallow the combo key, so we
    /// inject a harmless unassigned key while Win is still held: the shell sees a chord and
    /// stays quiet, and the Win key state never gets stuck.
    /// </summary>
    private static uint InjectBenignChordKey()
    {
        var inputs = new[]
        {
            new NativeMethods.INPUT
            {
                type = NativeMethods.INPUT_KEYBOARD,
                u = new NativeMethods.KEYBDINPUT { wVk = VkUnassigned, dwExtraInfo = (IntPtr)NativeMethods.InjectedExtraInfo }
            },
            new NativeMethods.INPUT
            {
                type = NativeMethods.INPUT_KEYBOARD,
                u = new NativeMethods.KEYBDINPUT { wVk = VkUnassigned, dwFlags = NativeMethods.KEYEVENTF_KEYUP, dwExtraInfo = (IntPtr)NativeMethods.InjectedExtraInfo }
            }
        };
        return NativeMethods.SendInput(2, inputs, Marshal.SizeOf<NativeMethods.INPUT>());
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
        if (kbd.dwExtraInfo == (IntPtr)NativeMethods.InjectedExtraInfo)
            return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);

        int vk = (int)kbd.vkCode;

        // Swallow key-ups of combo keys we swallowed on the way down (no orphan key-ups).
        if (isUp && _swallowedKeys.Remove(vk, out string? keyUpCorrelation))
        {
            Logger.ActivationInfo($"Swallowed matching key-up; vk=0x{vk:X2}.", keyUpCorrelation);
            return (IntPtr)1;
        }
        if (!isDown)
            return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);

        foreach (var rule in _rules)
        {
            if (vk != rule.Vk || !ModifiersMatch(rule))
                continue;

            long receiptTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
            string correlationId = Logger.NewCorrelationId();
            Logger.ActivationInfo($"Recognized hotkey {rule.Name}; overlayOpen={IsOverlayOpen}; foreground={DescribeForeground()}.", correlationId);
            var request = new HotkeyEventArgs(rule, receiptTimestamp, correlationId);

            // The combo's own native UI (Task View, Game Bar…) is foreground: let the key
            // reach it so the native UI toggles itself closed instead of reopening our overlay.
            if (IsForegroundNativeUi(rule, out string nativeUiReason))
            {
                Logger.ActivationInfo($"Foreground native UI matched ({nativeUiReason}); passing key through unchanged.", correlationId);
                return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
            }
            Logger.ActivationInfo("Foreground native-UI check did not match.", correlationId);

            if (IsOverlayOpen)
            {
                Logger.ActivationInfo($"Overlay already open; notifying pass-through listeners; passThrough={PassThroughOnSecondPress}.", correlationId);
                HotkeyPassthrough?.Invoke(this, request);
                if (PassThroughOnSecondPress)
                {
                    Logger.ActivationInfo("Second press passed through to Windows.", correlationId);
                    return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
                }
                // Close-only mode: swallow the combo so its native UI never fires.
                SwallowKey(vk, rule, correlationId);
                return (IntPtr)1;
            }

            Logger.ActivationInfo("Overlay hidden; notifying overlay-open listeners.", correlationId);
            HotkeyOpenRequested?.Invoke(this, request);
            SwallowKey(vk, rule, correlationId);
            return (IntPtr)1;
        }

        return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    private void SwallowKey(int vk, HotkeyRule rule, string correlationId)
    {
        _swallowedKeys[vk] = correlationId;
        Logger.ActivationInfo($"Swallowed key-down; vk=0x{vk:X2}.", correlationId);
        if (!rule.Win)
            return;

        uint sent = InjectBenignChordKey();
        Logger.ActivationInfo($"Injected benign Win chord; sent={sent}/2.", correlationId);
    }

    private static string DescribeForeground()
    {
        try
        {
            IntPtr foreground = NativeMethods.GetForegroundWindow();
            var className = new System.Text.StringBuilder(256);
            NativeMethods.GetClassName(foreground, className, className.Capacity);
            NativeMethods.GetWindowThreadProcessId(foreground, out uint pid);
            return $"handle={FormatHandle(foreground)}, class={className}, pid={pid}";
        }
        catch (Exception ex)
        {
            Logger.ActivationWarning($"Unable to inspect foreground window: {ex.GetType().Name}.");
            return "unavailable";
        }
    }

    private static bool IsForegroundNativeUi(HotkeyRule rule, out string reason)
    {
        reason = "no configured native UI";
        if (rule.NativeClassNames.Length == 0 && rule.NativeProcessNames.Length == 0)
            return false;

        IntPtr foreground = NativeMethods.GetForegroundWindow();
        if (foreground == IntPtr.Zero)
        {
            reason = "no foreground window";
            return false;
        }

        if (rule.NativeClassNames.Length > 0)
        {
            var className = new System.Text.StringBuilder(256);
            NativeMethods.GetClassName(foreground, className, className.Capacity);
            foreach (string name in rule.NativeClassNames)
            {
                if (string.Equals(className.ToString(), name, StringComparison.OrdinalIgnoreCase))
                {
                    reason = "window class";
                    return true;
                }
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
                    {
                        reason = "window process";
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                reason = $"process inspection failed ({ex.GetType().Name})";
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

    private static string FormatHandle(IntPtr handle) => $"0x{handle.ToInt64():X}";

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
    public HotkeyEventArgs(HotkeyRule rule, long receiptTimestamp, string correlationId)
    {
        Rule = rule;
        ReceiptTimestamp = receiptTimestamp;
        CorrelationId = correlationId;
    }

    public HotkeyRule Rule { get; }
    public long ReceiptTimestamp { get; }
    public string CorrelationId { get; }
}
