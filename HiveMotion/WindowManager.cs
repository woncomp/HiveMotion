using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;

namespace HiveMotion;

public class WindowManager
{
    private readonly AppItem _appItem;

    public WindowManager(AppItem appItem)
    {
        _appItem = appItem ?? throw new ArgumentNullException(nameof(appItem));
    }

    public IReadOnlyList<WindowItem> FindWindows()
    {
        var windows = new List<WindowItem>();
        var processes = Process.GetProcessesByName(_appItem.ProcessName);

        foreach (var process in processes)
        {
            try
            {
                var handles = FindTopLevelWindowsForProcess((uint)process.Id);
                foreach (var handle in handles)
                {
                    var title = new StringBuilder(512);
                    NativeMethods.GetWindowText(handle, title, title.Capacity);
                    if (title.Length > 0)
                    {
                        windows.Add(new WindowItem
                        {
                            Handle = handle,
                            Title = title.ToString()
                        });
                    }
                }
            }
            catch
            {
                // process may have exited during enumeration
            }
        }

        for (int i = 0; i < windows.Count; i++)
            windows[i].Index = i + 1;

        return windows;
    }

    public bool TryActivateOrLaunch()
    {
        var windows = FindWindows();
        if (windows.Count == 1)
        {
            ActivateWindow(windows[0].Handle);
            return true;
        }
        return false;
    }

    public void Launch()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = _appItem.ExecutablePath,
                UseShellExecute = true
            });
        }
        catch
        {
            // best effort
        }
    }

    public void ActivateWindow(IntPtr hWnd)
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

    private static List<IntPtr> FindTopLevelWindowsForProcess(uint pid)
    {
        var result = new List<IntPtr>();
        NativeMethods.EnumWindows((hWnd, _) =>
        {
            if (!NativeMethods.IsWindowVisible(hWnd))
                return true;

            NativeMethods.GetWindowThreadProcessId(hWnd, out uint windowPid);
            if (windowPid == pid)
                result.Add(hWnd);

            return true;
        }, IntPtr.Zero);
        return result;
    }
}
