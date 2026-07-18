using System;
using System.Diagnostics;

namespace HiveMotion;

public static class WindowManager
{
    public static void Launch(string executablePath)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = executablePath,
                UseShellExecute = true
            });
        }
        catch
        {
            // best effort
        }
    }

    public static void ActivateWindow(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero)
            return;

        if (NativeMethods.IsIconic(hWnd))
            NativeMethods.ShowWindow(hWnd, NativeMethods.SW_RESTORE);

        uint foregroundThread = NativeMethods.GetWindowThreadProcessId(NativeMethods.GetForegroundWindow(), out _);
        uint targetThread = NativeMethods.GetWindowThreadProcessId(hWnd, out _);
        uint currentThread = NativeMethods.GetCurrentThreadId();

        if (foregroundThread != targetThread)
        {
            NativeMethods.AttachThreadInput(foregroundThread, currentThread, true);
            NativeMethods.AttachThreadInput(targetThread, currentThread, true);
        }

        NativeMethods.SetForegroundWindow(hWnd);

        if (foregroundThread != targetThread)
        {
            NativeMethods.AttachThreadInput(targetThread, currentThread, false);
            NativeMethods.AttachThreadInput(foregroundThread, currentThread, false);
        }
    }
}
