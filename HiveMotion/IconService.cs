using System.Diagnostics;
using System.IO;
using System.Windows.Media;

namespace HiveMotion;

/// <summary>Shared cache-only selection and asynchronous resource preparation.</summary>
internal sealed class IconService : IDisposable
{
    private sealed record Asset(ImageSource Image, DateTime Stamp, long Length);
    public static IconService Shared { get; } = new();
    private readonly AsyncResourceCache<Asset> _cache;
    private readonly Dictionary<string, ImageSource> _glyphs = new(StringComparer.Ordinal);
    private long _hits;
    private long _misses;

    public IconService() => _cache = new(Load);

    internal IconService(Func<string, ImageSource?> load, Func<DateTimeOffset>? now = null)
    {
        _cache = new((path, previous) => load(path) is { } image
            ? new Asset(image, default, 0) : null, now);
    }

    public event Action<string>? Changed
    {
        add => _cache.Changed += value;
        remove => _cache.Changed -= value;
    }

    public void PrepareGlyphs()
    {
        // Called once on the UI thread before consumers and the keyboard hook exist.
        foreach (string glyph in SystemActions.All.Select(a => a.IconGlyph)
                     .Concat(new[] { "\uE71D", "\uE713", "\uE7C4" }).Distinct())
            _glyphs[glyph] = GlyphIcon.ForGlyph(glyph);
    }

    public ImageSource? TryGetCached(IconRequest request, ImageSource? runtimeIcon = null)
    {
        var custom = Read(request.CustomPath);
        var result = custom ?? runtimeIcon ?? Read(request.ExecutablePath);
        if (result == null && request.Glyph.Length > 0)
            _glyphs.TryGetValue(request.Glyph, out result);
        if (result == null) Interlocked.Increment(ref _misses);
        else Interlocked.Increment(ref _hits);
        return result;
    }

    private ImageSource? Read(string path) => Key(path) is { } key ? _cache.Read(key)?.Image : null;

    public void Request(IconRequest request, bool visible = true)
    {
        if (Key(request.CustomPath) is { } custom) _cache.Request(custom, visible);
        if (Key(request.ExecutablePath) is { } executable) _cache.Request(executable, visible);
    }

    public void Invalidate(IconRequest request)
    {
        if (Key(request.CustomPath) is { } custom) _cache.Invalidate(custom);
        if (Key(request.ExecutablePath) is { } executable) _cache.Invalidate(executable);
    }

    public bool References(IconRequest request, string key) =>
        string.Equals(Key(request.CustomPath), key, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Key(request.ExecutablePath), key, StringComparison.OrdinalIgnoreCase);

    // Pure path syntax normalization; file existence and versions are worker-only.
    private static string? Key(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(Environment.SystemDirectory, path));
        }
        catch (ArgumentException) { return null; }
        catch (NotSupportedException) { return null; }
        catch (PathTooLongException) { return null; }
    }

    private static Asset? Load(string path, Asset? previous)
    {
        long start = Stopwatch.GetTimestamp();
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists) return null;
            var stamp = file.LastWriteTimeUtc;
            long length = file.Length;
            long metadataDone = Stopwatch.GetTimestamp();
            if (previous != null && previous.Stamp == stamp && previous.Length == length)
                return previous;
            var image = IconHelper.LoadFile(path);
            if (Logger.IsVerboseEnabled)
                Logger.Info($"icon-load metadata={Stopwatch.GetElapsedTime(start, metadataDone).TotalMilliseconds:F1}ms " +
                    $"extract-decode={Stopwatch.GetElapsedTime(metadataDone).TotalMilliseconds:F1}ms " +
                    $"thread={Environment.CurrentManagedThreadId} success={image != null}");
            if (image == null) return null;
            image.Freeze();
            // A replacement during extraction must not be published as the checked version.
            file.Refresh();
            return file.Exists && file.LastWriteTimeUtc == stamp && file.Length == length
                ? new Asset(image, stamp, length) : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    public void Dispose()
    {
        _cache.Dispose();
        Logger.Info($"icon-cache hits={Interlocked.Read(ref _hits)} misses={Interlocked.Read(ref _misses)}");
    }
}
