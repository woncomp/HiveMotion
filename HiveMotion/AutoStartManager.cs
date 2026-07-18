using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using Microsoft.Win32;

namespace HiveMotion;

public class AutoStartManager
{
    private const string RegistryRunKey = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
    private const string AppName = "HiveMotion";

    public bool IsAutoStartEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegistryRunKey, false);
        if (key == null)
            return false;

        string? value = key.GetValue(AppName) as string;
        if (string.IsNullOrEmpty(value))
            return false;

        return value!.Contains(GetExecutablePath(), StringComparison.OrdinalIgnoreCase);
    }

    public void EnableAutoStart()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegistryRunKey, true)
            ?? Registry.CurrentUser.CreateSubKey(RegistryRunKey);
        key?.SetValue(AppName, $"\"{GetExecutablePath()}\"");
    }

    public void DisableAutoStart()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegistryRunKey, true);
        key?.DeleteValue(AppName, false);
    }

    private static string GetExecutablePath()
    {
        return Process.GetCurrentProcess().MainModule?.FileName
            ?? Assembly.GetExecutingAssembly().Location;
    }
}
