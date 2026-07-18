using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace HiveMotion;

public static class IconHelper
{
    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static ImageSource? ForWindow(IntPtr hWnd, Process process)
    {
        IntPtr hIcon = NativeMethods.SendMessage(hWnd, NativeMethods.WM_GETICON, (IntPtr)NativeMethods.ICON_SMALL2, IntPtr.Zero);
        if (hIcon == IntPtr.Zero)
            hIcon = NativeMethods.SendMessage(hWnd, NativeMethods.WM_GETICON, (IntPtr)NativeMethods.ICON_BIG, IntPtr.Zero);
        if (hIcon == IntPtr.Zero)
            hIcon = NativeMethods.GetClassLongPtr64(hWnd, NativeMethods.GCL_HICONSM);
        if (hIcon == IntPtr.Zero)
            hIcon = NativeMethods.GetClassLongPtr64(hWnd, NativeMethods.GCL_HICON);
        if (hIcon != IntPtr.Zero)
            return FromHIcon(hIcon);

        return ForProcess(process);
    }

    public static ImageSource? ForProcess(Process process)
    {
        string? path = null;
        try
        {
            path = process.MainModule?.FileName;
        }
        catch
        {
            // access denied for protected processes; fall back to no icon
        }
        return path == null ? null : ForExecutable(path);
    }

    public static ImageSource? ForExecutable(string executablePath)
    {
        if (Cache.TryGetValue(executablePath, out var cached))
            return cached;

        ImageSource? result = null;
        try
        {
            string path = executablePath;
            if (!Path.IsPathRooted(path))
            {
                string systemPath = Path.Combine(Environment.SystemDirectory, path);
                if (File.Exists(systemPath))
                    path = systemPath;
            }

            using (var icon = System.Drawing.Icon.ExtractAssociatedIcon(path))
            {
                if (icon != null)
                    result = FromHIcon(icon.Handle);
            }
        }
        catch
        {
            // no icon available
        }

        Cache[executablePath] = result;
        return result;
    }

    private static ImageSource FromHIcon(IntPtr hIcon)
    {
        var source = Imaging.CreateBitmapSourceFromHIcon(
            hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        source.Freeze();
        return source;
    }
}
