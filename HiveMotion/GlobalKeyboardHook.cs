using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;

namespace HiveMotion;

/// <summary>
/// Global low-level keyboard hook driven by a list of <see cref="HotkeyRule"/> combos.
/// Hidden overlay: swallow the combo and ask to open, except when Win+Tab is confirmed to
/// belong to Task View. Open overlay: swallow the combo and ask to close.
/// </summary>
public sealed class GlobalKeyboardHook : IDisposable
{
    private const int VkUnassigned = 0xE8;
    private const string TaskViewClassWin11 = "XamlExplorerHostIslandWindow";
    private const string TaskViewClassLegacy = "MultitaskingViewFrame";
    private const string TaskViewTitleEnglish = "Task View";
    private const string TaskViewTitleChinese = "任务视图";

    private readonly IReadOnlyList<HotkeyRule> _rules;
    private readonly Dictionary<int, string> _swallowedKeys = new();
    private readonly ManualResetEvent _ready = new(false);
    private Thread? _hookThread;
    private uint _hookThreadId;
    private IntPtr _hookHandle = IntPtr.Zero;
    private NativeMethods.LowLevelKeyboardProc? _hookProc;
    private bool _disposed;

    /// <summary>Mirror of the overlay visibility, written by the app; decides open vs close.</summary>
    public bool IsOverlayOpen { get; set; }

    /// <summary>A registered combo fired while the overlay was hidden: open the overlay.</summary>
    public event EventHandler<HotkeyEventArgs>? HotkeyOpenRequested;
    /// <summary>A registered combo fired while the overlay was open: close the overlay.</summary>
    public event EventHandler<HotkeyEventArgs>? HotkeyCloseRequested;

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
            // DescribeForeground performs several P/Invoke calls; evaluate it only when the
            // log entry can actually be emitted (the callback must stay cheap when verbose is off).
            if (Logger.IsVerboseEnabled)
                Logger.ActivationInfo($"Recognized hotkey {rule.Name}; overlayOpen={IsOverlayOpen}; foreground={DescribeForeground()}.", correlationId);
            var request = new HotkeyEventArgs(rule, receiptTimestamp, correlationId);

            if (IsOverlayOpen)
            {
                Logger.ActivationInfo("Overlay already open; notifying close listeners and swallowing the hotkey.", correlationId);
                HotkeyCloseRequested?.Invoke(this, request);
                SwallowKey(vk, rule, correlationId);
                return (IntPtr)1;
            }

            // Task View can be opened independently from HiveMotion through the taskbar.
            // Only a strictly confirmed Task View receives Win+Tab so Windows can close it.
            if (IsWinTab(rule))
            {
                if (IsForegroundTaskView(out string taskViewReason))
                {
                    Logger.ActivationInfo($"Foreground Task View confirmed ({taskViewReason}); passing Win+Tab through unchanged.", correlationId);
                    return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
                }
                Logger.ActivationInfo($"Foreground Task View not confirmed ({taskViewReason}); opening the overlay.", correlationId);
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
            var title = new System.Text.StringBuilder(512);
            NativeMethods.GetClassName(foreground, className, className.Capacity);
            NativeMethods.GetWindowText(foreground, title, title.Capacity);
            NativeMethods.GetWindowThreadProcessId(foreground, out uint pid);
            return $"handle={FormatHandle(foreground)}, class={className}, title=\"{SanitizeLogValue(title.ToString())}\", pid={pid}";
        }
        catch (Exception ex)
        {
            Logger.ActivationWarning($"Unable to inspect foreground window: {ex.GetType().Name}.");
            return "unavailable";
        }
    }

    private static bool IsForegroundTaskView(out string reason)
    {
        IntPtr initialForeground = NativeMethods.GetForegroundWindow();
        if (initialForeground == IntPtr.Zero)
        {
            reason = "no foreground window";
            return false;
        }

        if (!NativeMethods.IsWindowVisible(initialForeground))
        {
            reason = "foreground window is not visible";
            return false;
        }

        var className = new System.Text.StringBuilder(256);
        if (NativeMethods.GetClassName(initialForeground, className, className.Capacity) == 0)
        {
            reason = "unable to read foreground window class";
            return false;
        }
        string classValue = className.ToString();
        if (!string.Equals(classValue, TaskViewClassWin11, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(classValue, TaskViewClassLegacy, StringComparison.OrdinalIgnoreCase))
        {
            reason = $"unsupported class={classValue}";
            return false;
        }

        var title = new System.Text.StringBuilder(512);
        if (NativeMethods.GetWindowText(initialForeground, title, title.Capacity) == 0)
        {
            reason = $"empty title; class={classValue}";
            return false;
        }
        string titleValue = title.ToString();
        if (!string.Equals(titleValue, TaskViewTitleEnglish, StringComparison.Ordinal) &&
            !string.Equals(titleValue, TaskViewTitleChinese, StringComparison.Ordinal))
        {
            reason = $"unsupported title=\"{SanitizeLogValue(titleValue)}\"; class={classValue}";
            return false;
        }

        NativeMethods.GetWindowThreadProcessId(initialForeground, out uint foregroundPid);
        IntPtr shellWindow = NativeMethods.GetShellWindow();
        if (shellWindow == IntPtr.Zero)
        {
            reason = $"no shell window; class={classValue}; title=\"{SanitizeLogValue(titleValue)}\"; pid={foregroundPid}";
            return false;
        }
        NativeMethods.GetWindowThreadProcessId(shellWindow, out uint shellPid);
        if (foregroundPid == 0 || shellPid == 0 || foregroundPid != shellPid)
        {
            reason = $"shell PID mismatch; class={classValue}; title=\"{SanitizeLogValue(titleValue)}\"; pid={foregroundPid}; shellPid={shellPid}";
            return false;
        }

        try
        {
            int result = NativeMethods.DwmGetWindowAttribute(
                initialForeground, NativeMethods.DWMWA_CLOAKED, out int cloaked, sizeof(int));
            if (result != 0)
            {
                reason = $"cloak inspection failed; hresult=0x{result:X8}; class={classValue}; title=\"{SanitizeLogValue(titleValue)}\"; pid={foregroundPid}";
                return false;
            }
            if (cloaked != 0)
            {
                reason = $"foreground window is cloaked; class={classValue}; title=\"{SanitizeLogValue(titleValue)}\"; pid={foregroundPid}";
                return false;
            }
        }
        catch (Exception ex)
        {
            reason = $"cloak inspection threw {ex.GetType().Name}; class={classValue}; title=\"{SanitizeLogValue(titleValue)}\"; pid={foregroundPid}";
            return false;
        }

        IntPtr finalForeground = NativeMethods.GetForegroundWindow();
        if (finalForeground != initialForeground)
        {
            reason = $"foreground changed during inspection; initial={FormatHandle(initialForeground)}; final={FormatHandle(finalForeground)}";
            return false;
        }

        reason = $"handle={FormatHandle(initialForeground)}; class={classValue}; title=\"{SanitizeLogValue(titleValue)}\"; pid={foregroundPid}";
        return true;
    }

    private static bool IsWinTab(HotkeyRule rule)
    {
        return rule.Win && !rule.Ctrl && !rule.Alt && !rule.Shift && rule.Vk == NativeMethods.VK_TAB;
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

    private static string SanitizeLogValue(string value) => value.Replace('\r', ' ').Replace('\n', ' ');

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
