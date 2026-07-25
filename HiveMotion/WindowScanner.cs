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

    /// <summary>
    /// Locale-neutral brand names. Names that should follow the UI language
    /// (Notepad, Paint, Explorer…) live in the resx under "AppName_{process}" instead.
    /// </summary>
    private static readonly Dictionary<string, string> DisplayNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["msedge"] = "Edge",
        ["code"] = "VS Code",
        ["devenv"] = "Visual Studio",
        ["powershell"] = "PowerShell",
        ["pwsh"] = "PowerShell",
        ["chrome"] = "Chrome",
        ["firefox"] = "Firefox",
        ["winword"] = "Word",
        ["excel"] = "Excel",
        ["powerpnt"] = "PowerPoint",
        ["outlook"] = "Outlook",
        ["qq"] = "QQ"
    };

    private readonly IReadOnlyList<string> _priorityProcessNames;
    private readonly Dictionary<ProcessInstanceKey, ProcessMetadata> _processMetadata = new();

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

            if (!TryGetProcessKey(process, pid, out var key))
                continue; // An unknown lifetime must never be used for exact pinned matching.

            if (!_processMetadata.TryGetValue(key, out var metadata))
            {
                try
                {
                    string discoveredProcessName = process.ProcessName;
                    string? executablePath = ProcessIdentity.TryGetImagePath(pid);
                    string? arguments = null;
                    string? workingDirectory = null;
                    if (ProcessIdentity.TryReadProcessParameters(pid, out string? commandLine, out string? currentDirectory))
                    {
                        arguments = ProcessIdentity.ExtractArguments(commandLine);
                        workingDirectory = currentDirectory;
                    }
                    metadata = new ProcessMetadata(discoveredProcessName, executablePath, arguments, workingDirectory,
                        executablePath == null ? null : IconHelper.ForExecutable(executablePath));
                    _processMetadata[key] = metadata;
                }
                catch
                {
                    continue;
                }
            }

            string processName = metadata.ProcessName;

            if (BlockedProcesses.Contains(processName))
                continue;

            string appName = ResolveAppName(processName, title);

            result.Add(new RunningWindow
            {
                Handle = handle,
                ProcessId = pid,
                ProcessCreationFileTime = key.CreationFileTime,
                ProcessName = processName,
                AppName = appName,
                Title = title,
                Icon = metadata.Icon ?? IconHelper.ForWindow(handle, process),
                Priority = PriorityOf(processName),
                ZOrder = zOrder,
                PreferredLetter = PreferredLetter(appName, processName),
                ExecutablePath = metadata.ExecutablePath,
                CommandLineArguments = metadata.Arguments,
                WorkingDirectory = metadata.WorkingDirectory
            });
        }

        var liveKeys = new HashSet<ProcessInstanceKey>(result.Select(w => new ProcessInstanceKey(w.ProcessId, w.ProcessCreationFileTime)));
        foreach (var staleKey in _processMetadata.Keys.Where(key => !liveKeys.Contains(key)).ToList())
            _processMetadata.Remove(staleKey);

        return result;
    }

    private static bool TryGetProcessKey(Process process, uint pid, out ProcessInstanceKey key)
    {
        key = default;
        try
        {
            if (!NativeMethods.GetProcessTimes(process.Handle, out long created, out _, out _, out _) || created == 0)
                return false;
            key = new ProcessInstanceKey(pid, created);
            return true;
        }
        catch { return false; }
    }

    private sealed record ProcessMetadata(string ProcessName, string? ExecutablePath, string? Arguments,
        string? WorkingDirectory, System.Windows.Media.ImageSource? Icon);

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

        if (Loc.TryGet("AppName_" + processName.ToLowerInvariant()) is { } localized)
            return localized;

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
