using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace HiveMotion;

/// <summary>Enumerates the visible top-level windows that make sense in a task switcher.</summary>
public sealed class WindowScanner
{
    private static readonly HashSet<string> BlockedClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd",
        "Xaml_WindowedPopupClass", "DV2ControlHost",
        "MsgrIMEWindowClass", "SysShadow", "Button", "MsoSplash"
    };

    private static readonly HashSet<string> BlockedProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "TextInputHost", "ChsIME", "sihost", "ShellExperienceHost", "ShellHost",
        "SearchHost", "StartMenuExperienceHost", "MoNotificationUx", "TabTip"
    };

    private static readonly Dictionary<string, string> DisplayNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["notepad"] = "记事本",
        ["msedge"] = "Edge",
        ["code"] = "VS Code",
        ["explorer"] = "资源管理器",
        ["devenv"] = "Visual Studio",
        ["windowsterminal"] = "终端",
        ["cmd"] = "命令提示符",
        ["powershell"] = "PowerShell",
        ["pwsh"] = "PowerShell",
        ["chrome"] = "Chrome",
        ["firefox"] = "Firefox",
        ["winword"] = "Word",
        ["excel"] = "Excel",
        ["powerpnt"] = "PowerPoint",
        ["outlook"] = "Outlook",
        ["mspaint"] = "画图",
        ["wechat"] = "微信",
        ["qq"] = "QQ"
    };

    private readonly IReadOnlyList<string> _priorityProcessNames;

    public WindowScanner(IReadOnlyList<string> priorityProcessNames)
    {
        _priorityProcessNames = priorityProcessNames;
    }

    public IReadOnlyList<RunningWindow> Scan()
    {
        var candidates = new List<(IntPtr Handle, string Title, int ZOrder)>();
        int z = 0;
        NativeMethods.EnumWindows((hWnd, _) =>
        {
            if (IsCandidate(hWnd, out string? title))
                candidates.Add((hWnd, title!, z++));
            return true;
        }, IntPtr.Zero);

        uint ownPid = NativeMethods.GetCurrentProcessId();
        var result = new List<RunningWindow>();

        foreach (var (handle, title, zOrder) in candidates)
        {
            NativeMethods.GetWindowThreadProcessId(handle, out uint pid);
            if (pid == 0 || pid == ownPid)
                continue;

            Process process;
            try
            {
                process = Process.GetProcessById((int)pid);
            }
            catch
            {
                continue;
            }

            string processName;
            try
            {
                processName = process.ProcessName;
            }
            catch
            {
                continue;
            }

            if (BlockedProcesses.Contains(processName))
                continue;

            string appName = ResolveAppName(processName, title);

            // Launch identity, used by pinned-cell matching. Best effort: elevated or
            // protected processes refuse the PEB read and simply stay unpinnable-by-args.
            string? executablePath = ProcessIdentity.TryGetImagePath(pid);
            string? arguments = null;
            if (ProcessIdentity.TryReadProcessParameters(pid, out string? commandLine, out _))
                arguments = ProcessIdentity.ExtractArguments(commandLine);

            result.Add(new RunningWindow
            {
                Handle = handle,
                ProcessId = pid,
                ProcessName = processName,
                AppName = appName,
                Title = title,
                Icon = IconHelper.ForWindow(handle, process),
                Priority = PriorityOf(processName),
                ZOrder = zOrder,
                PreferredLetter = PreferredLetter(appName, processName),
                ExecutablePath = executablePath,
                CommandLineArguments = arguments
            });
        }

        return result;
    }

    private static bool IsCandidate(IntPtr hWnd, out string? title)
    {
        title = null;
        if (!NativeMethods.IsWindowVisible(hWnd))
            return false;

        var titleBuilder = new StringBuilder(512);
        if (NativeMethods.GetWindowText(hWnd, titleBuilder, titleBuilder.Capacity) == 0)
            return false;
        if (string.IsNullOrWhiteSpace(titleBuilder.ToString()))
            return false;

        var classBuilder = new StringBuilder(256);
        NativeMethods.GetClassName(hWnd, classBuilder, classBuilder.Capacity);
        if (BlockedClasses.Contains(classBuilder.ToString()))
            return false;

        int exStyle = NativeMethods.GetWindowLong(hWnd, NativeMethods.GWL_EXSTYLE);
        if ((exStyle & NativeMethods.WS_EX_TOOLWINDOW) != 0)
            return false;

        try
        {
            if (NativeMethods.DwmGetWindowAttribute(hWnd, NativeMethods.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
                return false;
        }
        catch
        {
            // DWM unavailable; ignore cloak state
        }

        title = titleBuilder.ToString();
        return true;
    }

    private static string ResolveAppName(string processName, string title)
    {
        // UWP windows all share ApplicationFrameHost; the window title is the only useful name.
        if (processName.Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase))
            return title;

        if (DisplayNames.TryGetValue(processName, out string? display))
            return display;

        return char.ToUpperInvariant(processName[0]) + processName.Substring(1);
    }

    private int PriorityOf(string processName)
    {
        for (int i = 0; i < _priorityProcessNames.Count; i++)
        {
            if (_priorityProcessNames[i].Equals(processName, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return 100;
    }

    private static char? PreferredLetter(string appName, string processName)
    {
        char? letter = FirstAsciiLetter(appName);
        if (letter.HasValue)
            return letter;
        return FirstAsciiLetter(processName);
    }

    private static char? FirstAsciiLetter(string text)
    {
        foreach (char c in text)
        {
            if (c is >= 'a' and <= 'z')
                return char.ToUpperInvariant(c);
            if (c is >= 'A' and <= 'Z')
                return c;
        }
        return null;
    }
}
