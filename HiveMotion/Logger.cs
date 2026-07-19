using System;
using System.IO;

namespace HiveMotion;

/// <summary>Append-only diagnostics log at %TEMP%\HiveMotion\hivemotion.log.</summary>
internal static class Logger
{
    private static readonly string LogPath = Path.Combine(Path.GetTempPath(), "HiveMotion", "hivemotion.log");

    public static void Info(string message) => Write("INFO", message);

    public static void Error(Exception ex) => Write("ERROR", ex.ToString());

    private static void Write(string level, string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {message}\r\n");
        }
        catch
        {
            // never let logging take the app down
        }
    }
}
