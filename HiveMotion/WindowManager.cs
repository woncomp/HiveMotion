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
