using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace HiveMotion;

public enum LogLevel
{
    Info,
    Warning,
    Error
}

public enum LogChannel
{
    Default,
    Activation
}

public sealed record LogEntry(DateTime Timestamp, LogLevel Level, LogChannel Channel, string Message, string? CorrelationId)
{
    public string DisplayText => $"{Timestamp:MMdd HH:mm:ss} {LevelToLetter(Level)} [{Channel.ToString().ToUpperInvariant()}] {Message}";

    private static char LevelToLetter(LogLevel level) => level switch
    {
        LogLevel.Info => 'I',
        LogLevel.Warning => 'W',
        LogLevel.Error => 'E',
        _ => '?'
    };
}

/// <summary>
/// Bounded asynchronous diagnostics log at %LOCALAPPDATA%\HiveMotion\Logs.
/// Verbose diagnostics (Info/Warning) are gated by <see cref="IsVerboseEnabled"/>
/// so the hot activation path stays cheap when logging is off. Errors always log.
/// </summary>
internal static class Logger
{
    private const int Capacity = 256;
    private const int RetainedLogFiles = 7;

    private static readonly object QueueLock = new();
    private static readonly object FileLock = new();
    private static readonly AutoResetEvent WakeWriter = new(false);
    private static readonly Queue<PendingEntry> Queue = new(Capacity);
    private static readonly List<LogEntry> SessionEntries = new();
    private static readonly object SessionLock = new();
    private static readonly Thread Writer = new(WriteLoop)
    {
        IsBackground = true,
        Name = "HiveMotionLogWriter"
    };

    private static long _nextCorrelationId;
    private static int _completed;

    public static event EventHandler<LogEntry>? EntryWritten;

    /// <summary>
    /// Runtime switch controlling verbose diagnostics. When false, Info/Warning/Activation*
    /// calls return immediately without allocating or enqueuing. Errors always log.
    /// </summary>
    public static bool IsVerboseEnabled { get; set; }

    public static string LogDirectoryPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HiveMotion", "Logs");

    public static string ActiveLogPath => Path.Combine(LogDirectoryPath, $"hivemotion-{DateTime.Now:yyyy-MM-dd}.log");

    public static string NewCorrelationId() => $"HK-{Interlocked.Increment(ref _nextCorrelationId):D5}";

    static Logger() => Writer.Start();

    /// <summary>Bounded and nonblocking; safe to call from the low-level keyboard hook.</summary>
    public static void Info(string message, string? correlationId = null, LogChannel channel = LogChannel.Default)
    {
        if (!IsVerboseEnabled)
            return;
        Enqueue(LogLevel.Info, channel, message, correlationId, preserve: false);
    }

    public static void ActivationInfo(string message, string? correlationId = null)
    {
        if (!IsVerboseEnabled)
            return;
        Enqueue(LogLevel.Info, LogChannel.Activation, message, correlationId, preserve: false);
    }

    public static void Warning(string message, string? correlationId = null, LogChannel channel = LogChannel.Default)
    {
        if (!IsVerboseEnabled)
            return;
        Enqueue(LogLevel.Warning, channel, message, correlationId, preserve: false);
    }

    public static void ActivationWarning(string message, string? correlationId = null)
    {
        if (!IsVerboseEnabled)
            return;
        Enqueue(LogLevel.Warning, LogChannel.Activation, message, correlationId, preserve: false);
    }

    /// <summary>Queues an error without scheduling unbounded ThreadPool work. Errors always log.</summary>
    public static void Error(Exception ex, string? context = null, string? correlationId = null,
        LogChannel channel = LogChannel.Default)
    {
        string text = string.IsNullOrWhiteSpace(context) ? ex.ToString() : $"{context}: {ex}";
        Enqueue(LogLevel.Error, channel, text, correlationId, preserve: true);
    }

    public static void ActivationError(Exception ex, string? context = null, string? correlationId = null) =>
        Error(ex, context, correlationId, LogChannel.Activation);

    public static IReadOnlyList<LogEntry> GetSessionEntries()
    {
        lock (SessionLock)
            return SessionEntries.ToArray();
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

    private static void Enqueue(LogLevel level, LogChannel channel, string message, string? correlationId, bool preserve)
    {
        if (Volatile.Read(ref _completed) != 0)
            return;

        var entry = new PendingEntry(level, channel, message, correlationId);
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
            Queue.Enqueue(entry);
        }
        WakeWriter.Set();
    }

    private static void WriteLoop()
    {
        while (true)
        {
            PendingEntry? item = null;
            lock (QueueLock)
            {
                if (Queue.Count > 0)
                    item = Queue.Dequeue();
                else if (_completed != 0)
                    return;
            }

            if (item is { } entry)
                Write(entry);
            else
                WakeWriter.WaitOne();
        }
    }

    private static void Write(PendingEntry pending)
    {
        try
        {
            Directory.CreateDirectory(LogDirectoryPath);
            var entry = new LogEntry(DateTime.Now, pending.Level, pending.Channel, pending.Message, pending.CorrelationId);

            lock (FileLock)
            {
                File.AppendAllText(ActiveLogPath, entry.DisplayText + Environment.NewLine);
                TrimOldLogs();
            }

            lock (SessionLock)
                SessionEntries.Add(entry);

            NotifyListeners(entry);
        }
        catch
        {
            // never let logging take the app down
        }
    }

    private static void NotifyListeners(LogEntry entry)
    {
        Delegate[] listeners = EntryWritten?.GetInvocationList() ?? Array.Empty<Delegate>();
        foreach (Delegate candidate in listeners)
        {
            try
            {
                ((EventHandler<LogEntry>)candidate)(null, entry);
            }
            catch
            {
                // A viewer listener must not be able to break the producer thread.
            }
        }
    }

    private static void TrimOldLogs()
    {
        foreach (string path in Directory.EnumerateFiles(LogDirectoryPath, "hivemotion-*.log")
                     .OrderByDescending(File.GetCreationTimeUtc)
                     .Skip(RetainedLogFiles))
        {
            try
            {
                File.Delete(path);
            }
            catch
            {
                // A locked file will be retried on the next write.
            }
        }
    }

    private sealed record PendingEntry(LogLevel Level, LogChannel Channel, string Message, string? CorrelationId);
}
