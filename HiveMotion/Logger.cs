using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace HiveMotion;

/// <summary>Bounded asynchronous diagnostics log at %TEMP%\HiveMotion\hivemotion.log.</summary>
internal static class Logger
{
    private const int Capacity = 256;
    private static readonly string LogPath = Path.Combine(Path.GetTempPath(), "HiveMotion", "hivemotion.log");
    private static readonly object QueueLock = new();
    private static readonly Queue<(string Level, string Message)> Queue = new(Capacity);
    private static readonly AutoResetEvent WakeWriter = new(false);
    private static readonly object FileLock = new();
    private static readonly Thread Writer = new(WriteLoop) { IsBackground = true, Name = "HiveMotionLogWriter" };
    private static int _completed;

    static Logger() => Writer.Start();

    /// <summary>Bounded and nonblocking; safe to call from the low-level keyboard hook.</summary>
    public static void Info(string message) => Enqueue("INFO", message, preserve: false);

    /// <summary>Queues an error without scheduling unbounded ThreadPool work.</summary>
    public static void Error(Exception ex) => Enqueue("ERROR", ex.ToString(), preserve: true);

    private static void Enqueue(string level, string message, bool preserve)
    {
        if (Volatile.Read(ref _completed) != 0)
            return;

        lock (QueueLock)
        {
            if (_completed != 0)
                return;
            if (Queue.Count == Capacity)
            {
                if (!preserve)
                    return;
                // Prefer the newest error over stale informational traffic. This is bounded,
                // does no I/O on the producer, and keeps hook callbacks responsive.
                Queue.Dequeue();
            }
            Queue.Enqueue((level, message));
        }
        WakeWriter.Set();
    }

    /// <summary>Stops accepting producers and drains queued diagnostics for a bounded time.</summary>
    public static void Shutdown()
    {
        if (Interlocked.Exchange(ref _completed, 1) != 0)
            return;
        WakeWriter.Set();
        if (Writer.IsAlive)
            Writer.Join(TimeSpan.FromSeconds(1));
    }

    private static void WriteLoop()
    {
        while (true)
        {
            (string Level, string Message)? item = null;
            lock (QueueLock)
            {
                if (Queue.Count > 0)
                    item = Queue.Dequeue();
                else if (_completed != 0)
                    return;
            }

            if (item is { } entry)
                Write(entry.Level, entry.Message);
            else
                WakeWriter.WaitOne();
        }
    }

    private static void Write(string level, string message)
    {
        try
        {
            lock (FileLock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {message}\r\n");
            }
        }
        catch
        {
            // never let logging take the app down
        }
    }
}
