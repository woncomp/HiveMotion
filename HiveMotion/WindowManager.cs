using System;
using System.Diagnostics;
using System.IO;

namespace HiveMotion;

public static class WindowManager
{
    private const LogChannel ActivationChannel = LogChannel.Activation;

    /// <summary>Performs one foreground attempt without retrying or sleeping.</summary>
    /// <returns><see langword="true"/> only when the target is foreground after the attempt.</returns>
    public static bool ActivateWindowOnce(IntPtr hWnd, string? correlationId = null)
    {
        if (hWnd == IntPtr.Zero)
        {
            Logger.Warning("Window activation skipped because the handle was zero.", correlationId, ActivationChannel);
            return false;
        }

        IntPtr foregroundBefore = NativeMethods.GetForegroundWindow();
        if (foregroundBefore == hWnd)
        {
            Logger.Info($"Window activation skipped; target is already foreground. handle={FormatHandle(hWnd)}.", correlationId, ActivationChannel);
            return true;
        }

        Logger.Info($"Starting window activation; handle={FormatHandle(hWnd)}; foregroundBefore={FormatHandle(foregroundBefore)}.",
            correlationId, ActivationChannel);

        if (NativeMethods.IsIconic(hWnd))
        {
            NativeMethods.ShowWindow(hWnd, NativeMethods.SW_RESTORE);
            Logger.Info("Restored minimized target window before activation.", correlationId, ActivationChannel);
        }

        uint foregroundThread = foregroundBefore != IntPtr.Zero
            ? NativeMethods.GetWindowThreadProcessId(foregroundBefore, out _)
            : 0;
        uint targetThread = NativeMethods.GetWindowThreadProcessId(hWnd, out _);
        uint currentThread = NativeMethods.GetCurrentThreadId();

        bool attachedForeground = foregroundThread != 0
            && foregroundThread != currentThread
            && NativeMethods.AttachThreadInput(foregroundThread, currentThread, true);
        bool attachedTarget = targetThread != 0
            && targetThread != currentThread
            && targetThread != foregroundThread
            && NativeMethods.AttachThreadInput(targetThread, currentThread, true);

        if (attachedForeground || attachedTarget)
            Logger.Info($"Attached input threads; foreground={attachedForeground}; target={attachedTarget}.", correlationId, ActivationChannel);

        bool broughtToTop = false;
        bool setForeground = false;
        try
        {
            broughtToTop = NativeMethods.BringWindowToTop(hWnd);
            setForeground = NativeMethods.SetForegroundWindow(hWnd);
        }
        finally
        {
            if (attachedTarget)
                NativeMethods.AttachThreadInput(targetThread, currentThread, false);
            if (attachedForeground)
                NativeMethods.AttachThreadInput(foregroundThread, currentThread, false);
            if (attachedForeground || attachedTarget)
                Logger.Info("Detached input threads.", correlationId, ActivationChannel);
        }

        IntPtr foregroundAfter = NativeMethods.GetForegroundWindow();
        bool confirmed = foregroundAfter == hWnd;
        Logger.Info(
            $"Window activation completed; bringToTop={broughtToTop}; setForeground={setForeground}; " +
            $"foregroundAfter={FormatHandle(foregroundAfter)}; confirmed={confirmed}.",
            correlationId, ActivationChannel);
        return confirmed;
    }

    public static void Launch(string executablePath, string? arguments = null, string? workingDirectory = null)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                UseShellExecute = true
            };
            if (!string.IsNullOrEmpty(arguments))
                startInfo.Arguments = arguments;
            if (!string.IsNullOrEmpty(workingDirectory) && Directory.Exists(workingDirectory))
                startInfo.WorkingDirectory = workingDirectory;
            Process.Start(startInfo);
        }
        catch
        {
            // best effort
        }
    }

    /// <summary>
    /// Brings a window to the foreground. A background process is normally denied
    /// SetForegroundWindow, so we attach to the foreground thread's input queue first.
    /// The denial can still stick (elevated or hung foreground window, shell UI
    /// transitions), so the result is verified and retried before giving up.
    /// </summary>
    public static void ActivateWindow(IntPtr hWnd, string? correlationId = null)
    {
        if (hWnd == IntPtr.Zero)
        {
            Logger.Warning("Window activation skipped because the handle was zero.", correlationId, ActivationChannel);
            return;
        }

        Logger.Info($"Activating window with retry; handle={FormatHandle(hWnd)}.", correlationId, ActivationChannel);

        if (NativeMethods.IsIconic(hWnd))
        {
            NativeMethods.ShowWindow(hWnd, NativeMethods.SW_RESTORE);
            Logger.Info("Restored minimized target window before activation.", correlationId, ActivationChannel);
        }

        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (NativeMethods.GetForegroundWindow() == hWnd)
            {
                Logger.Info($"Target window became foreground after {attempt} attempt(s).", correlationId, ActivationChannel);
                return;
            }

            if (ActivateWindowOnce(hWnd, correlationId))
            {
                Logger.Info($"Target window became foreground after {attempt + 1} attempt(s).", correlationId, ActivationChannel);
                return;
            }

            System.Threading.Thread.Sleep(50);
        }

        Logger.Warning($"Foreground denied after retry for handle={FormatHandle(hWnd)}.", correlationId, ActivationChannel);
    }

    private static string FormatHandle(IntPtr handle) => $"0x{handle.ToInt64():X}";
}
