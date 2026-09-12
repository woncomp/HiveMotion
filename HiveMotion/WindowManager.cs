using System;
using System.Diagnostics;
using System.IO;

namespace HiveMotion;

public static class WindowManager
{
    internal static WindowActivationTarget? CaptureTarget(IntPtr handle)
    {
        if (handle == IntPtr.Zero || !NativeMethods.IsWindow(handle))
            return null;
        uint threadId = NativeMethods.GetWindowThreadProcessId(handle, out uint pid);
        if (threadId == 0)
            return null;
        IntPtr process = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == IntPtr.Zero)
            return null;
        try
        {
            if (!NativeMethods.GetProcessTimes(process, out long created, out _, out _, out _) || created == 0)
                return null;
            // Check ownership again in case the HWND changed while the process was queried.
            if (NativeMethods.GetWindowThreadProcessId(handle, out uint currentPid) != threadId || currentPid != pid)
                return null;
            return new WindowActivationTarget(handle, pid, created, threadId);
        }
        finally { NativeMethods.CloseHandle(process); }
    }

    internal static bool IsCurrentTarget(WindowActivationTarget target) =>
        CaptureTarget(target.Handle) is { } current && current == target;

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

}
