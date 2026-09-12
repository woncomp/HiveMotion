using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
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

/// <summary>
/// Bounded asynchronous diagnostics log at %LOCALAPPDATA%\HiveMotion\Logs.
/// Producers only stamp a high-resolution <see cref="Stopwatch"/> tick and push a struct
/// onto a lock-free queue; all formatting, wall-clock conversion, and file I/O happen in
/// batches on a dedicated writer thread. Verbose diagnostics (Info/Warning) are gated by
/// <see cref="IsVerboseEnabled"/> so the hot activation path stays cheap when logging is
/// off. Errors always log.
/// </summary>
internal static class Logger
{
    private const int Capacity = 256;
    private const int RetainedLogFiles = 7;

    private static readonly ConcurrentQueue<PendingEntry> Queue = new();
    private static readonly ManualResetEventSlim WakeWriter = new(false);
    private static readonly Thread Writer = new(WriteLoop)
    {
        IsBackground = true,
        Name = "HiveMotionLogWriter"
    };

    // Wall-clock anchor: producers capture Stopwatch ticks; the writer converts them to
    // local wall time, so producers never pay for DateTime work and timestamps are not
    // skewed by queue delays.
    private static readonly long BaseTimestamp = Stopwatch.GetTimestamp();
    private static readonly DateTime BaseUtc = DateTime.UtcNow;
    private static readonly double TicksToMilliseconds = 1000d / Stopwatch.Frequency;

    private static readonly UTF8Encoding FileEncoding = new(encoderShouldEmitUTF8Identifier: false);

    private static long _nextCorrelationId;
    private static int _approximateDepth;
    private static int _completed;
    private static volatile bool _isVerboseEnabled;

    /// <summary>
    /// Runtime switch controlling verbose diagnostics. When false, Info/Warning/Activation*
    /// calls return immediately without allocating or enqueuing. Errors always log.
    /// </summary>
    public static bool IsVerboseEnabled
    {
        get => _isVerboseEnabled;
        set => _isVerboseEnabled = value;
    }

    public static string LogDirectoryPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HiveMotion", "Logs");

    public static string ActiveLogPath => GetLogPath(DateTime.Now);

    private static string GetLogPath(DateTime timestamp) =>
        Path.Combine(LogDirectoryPath, $"hivemotion-{timestamp:yyyy-MM-dd}.log");

    public static string NewCorrelationId() => $"HK-{Interlocked.Increment(ref _nextCorrelationId):D5}";

    static Logger() => Writer.Start();

    /// <summary>Bounded and nonblocking; safe to call from the low-level keyboard hook.</summary>
    public static void Info(string message, string? correlationId = null, LogChannel channel = LogChannel.Default)
    {
        if (!_isVerboseEnabled)
            return;
        Enqueue(LogLevel.Info, channel, message, correlationId, preserve: false);
    }

    public static void ActivationInfo(string message, string? correlationId = null)
    {
        if (!_isVerboseEnabled)
            return;
        Enqueue(LogLevel.Info, LogChannel.Activation, message, correlationId, preserve: false);
    }

    public static void Warning(string message, string? correlationId = null, LogChannel channel = LogChannel.Default)
    {
        if (!_isVerboseEnabled)
            return;
        Enqueue(LogLevel.Warning, channel, message, correlationId, preserve: false);
    }

    public static void ActivationWarning(string message, string? correlationId = null)
    {
        if (!_isVerboseEnabled)
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

        // Capture event time before queueing; the writer converts it to wall time.
        long timestamp = Stopwatch.GetTimestamp();

        int depth = Interlocked.Increment(ref _approximateDepth);
        if (depth > Capacity)
        {
            if (!preserve)
            {
                Interlocked.Decrement(ref _approximateDepth);
                return;
            }
            // Prefer the newest error over stale informational traffic. The depth counter is
            // approximate under contention, which only makes the bound approximate too.
            if (Queue.TryDequeue(out _))
                Interlocked.Decrement(ref _approximateDepth);
        }

        Queue.Enqueue(new PendingEntry(timestamp, level, channel, message, correlationId));
        WakeWriter.Set();
    }

    private static void WriteLoop()
    {
        StreamWriter? stream = null;
        DateTime streamDate = default;
        var buffer = new StringBuilder(4096);
        try
        {
            while (Volatile.Read(ref _completed) == 0 || !Queue.IsEmpty)
            {
                try
                {
                    bool wroteAny = false;
                    while (Queue.TryDequeue(out PendingEntry entry))
                    {
                        Interlocked.Decrement(ref _approximateDepth);
                        DateTime wall = ToWallTime(entry.Timestamp);
                        if (stream == null || wall.Date != streamDate)
                        {
                            Flush(stream, buffer);
                            stream?.Dispose();
                            stream = OpenStream(wall);
                            streamDate = wall.Date;
                        }
                        AppendLine(buffer, wall, entry);
                        wroteAny = true;
                    }
                    if (wroteAny)
                        Flush(stream, buffer);
                }
                catch
                {
                    // Logging must never take the app down; drop the batch and keep going.
                    buffer.Clear();
                }

                WakeWriter.Wait();
                WakeWriter.Reset();
            }
        }
        finally
        {
            try
            {
                Flush(stream, buffer);
            }
            catch
            {
                // Nothing safe left to do during teardown.
            }
            stream?.Dispose();
        }
    }

    private static DateTime ToWallTime(long timestamp)
    {
        double elapsedMs = (timestamp - BaseTimestamp) * TicksToMilliseconds;
        return BaseUtc.AddMilliseconds(elapsedMs).ToLocalTime();
    }

    private static StreamWriter OpenStream(DateTime date)
    {
        Directory.CreateDirectory(LogDirectoryPath);
        TrimOldLogs();
        // ReadWrite sharing lets external tail tools (klogg, Get-Content -Wait) follow the
        // file while we hold it open.
        var fileStream = new FileStream(GetLogPath(date), FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        return new StreamWriter(fileStream, FileEncoding);
    }

    private static void Flush(StreamWriter? stream, StringBuilder buffer)
    {
        if (stream != null && buffer.Length > 0)
        {
            stream.Write(buffer);
            stream.Flush();
        }
        buffer.Clear();
    }

    private static void AppendLine(StringBuilder buffer, DateTime time, PendingEntry entry)
    {
        Append2(buffer, time.Month);
        Append2(buffer, time.Day);
        buffer.Append(' ');
        Append2(buffer, time.Hour);
        buffer.Append(':');
        Append2(buffer, time.Minute);
        buffer.Append(':');
        Append2(buffer, time.Second);
        buffer.Append('.');
        Append3(buffer, time.Millisecond);
        buffer.Append(' ');
        buffer.Append(LevelLetter(entry.Level));
        buffer.Append(' ');
        buffer.Append('[');
        buffer.Append(entry.Channel == LogChannel.Activation ? "ACTIVATION" : "DEFAULT");
        buffer.Append(']');
        buffer.Append(' ');
        buffer.Append(entry.Message);
        buffer.Append(Environment.NewLine);
    }

    private static void Append2(StringBuilder buffer, int value)
    {
        buffer.Append((char)('0' + value / 10));
        buffer.Append((char)('0' + value % 10));
    }

    private static void Append3(StringBuilder buffer, int value)
    {
        buffer.Append((char)('0' + value / 100));
        buffer.Append((char)('0' + value / 10 % 10));
        buffer.Append((char)('0' + value % 10));
    }

    private static char LevelLetter(LogLevel level) => level switch
    {
        LogLevel.Info => 'I',
        LogLevel.Warning => 'W',
        LogLevel.Error => 'E',
        _ => '?'
    };

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
                // A locked file will be retried on the next rollover.
            }
        }
    }

    private readonly record struct PendingEntry(long Timestamp, LogLevel Level, LogChannel Channel, string Message, string? CorrelationId);
}
