using System;
using System.Diagnostics;
using System.IO;

namespace HiveMotion;

public static class WindowManager
{
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
    public static void ActivateWindow(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero)
            return;

        if (NativeMethods.IsIconic(hWnd))
            NativeMethods.ShowWindow(hWnd, NativeMethods.SW_RESTORE);

        for (int attempt = 0; attempt < 3; attempt++)
        {
            IntPtr foreground = NativeMethods.GetForegroundWindow();
            if (foreground == hWnd)
                return;

            uint foregroundThread = foreground != IntPtr.Zero
                ? NativeMethods.GetWindowThreadProcessId(foreground, out _)
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

            NativeMethods.BringWindowToTop(hWnd);
            NativeMethods.SetForegroundWindow(hWnd);

            if (attachedTarget)
                NativeMethods.AttachThreadInput(targetThread, currentThread, false);
            if (attachedForeground)
                NativeMethods.AttachThreadInput(foregroundThread, currentThread, false);

            if (NativeMethods.GetForegroundWindow() == hWnd)
                return;

            System.Threading.Thread.Sleep(50);
        }

        Logger.Info($"ActivateWindow: foreground denied for hwnd 0x{hWnd.ToInt64():X}");
    }
}
