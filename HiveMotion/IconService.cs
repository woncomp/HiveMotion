using System.Diagnostics;
using System.IO;
using System.Windows.Media;

namespace HiveMotion;

/// <summary>Shared cache-only selection and asynchronous resource preparation.</summary>
internal sealed class IconService : IDisposable
{
    private sealed record Asset(ImageSource Image, DateTime Stamp, long Length);

    // Quantized size tiers keep the cache bounded while letting crisp per-DPI sizes
    // coexist. Selection picks the smallest tier that covers the requested pixels.
    private static readonly int[] Tiers = { 32, 48, 96 };
    private const char TierSeparator = '|';
    private const int DotsPerInch = 96;
    /// <summary>Request size for views that have no measurable rendered size yet.</summary>
    internal const double DefaultPixels = 48;

    public static IconService Shared { get; } = new();
    private readonly AsyncResourceCache<Asset> _cache;
    private readonly Dictionary<string, ImageSource> _glyphs = new(StringComparer.Ordinal);
    private long _hits;
    private long _misses;

    public IconService() => _cache = new(Load);

    internal IconService(Func<string, int, ImageSource?> load, Func<DateTimeOffset>? now = null)
    {
        _cache = new((key, previous) =>
        {
            Decompose(key, out string path, out int pixels);
            return load(path, pixels) is { } image ? new Asset(image, default, 0) : null;
        }, now);
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

    public ImageSource? TryGetCached(IconRequest request, double pixels, ImageSource? runtimeIcon = null)
    {
        int tier = Quantize(pixels);
        var custom = Read(request.CustomPath, tier);
        var result = custom ?? runtimeIcon ?? Read(request.ExecutablePath, tier);
        if (result == null && request.Glyph.Length > 0)
            _glyphs.TryGetValue(request.Glyph, out result);
        if (result == null) Interlocked.Increment(ref _misses);
        else Interlocked.Increment(ref _hits);
        return result;
    }

    private ImageSource? Read(string path, int tier) =>
        Key(path) is { } key ? _cache.Read(Compose(key, tier))?.Image : null;

    public void Request(IconRequest request, double pixels, bool visible = true)
    {
        int tier = Quantize(pixels);
        if (Key(request.CustomPath) is { } custom) _cache.Request(Compose(custom, tier), visible);
        if (Key(request.ExecutablePath) is { } executable) _cache.Request(Compose(executable, tier), visible);
    }

    public void Invalidate(IconRequest request)
    {
        // Evict every tier: file edits must not leave a stale asset at any size.
        foreach (int tier in Tiers)
        {
            if (Key(request.CustomPath) is { } custom) _cache.Invalidate(Compose(custom, tier));
            if (Key(request.ExecutablePath) is { } executable) _cache.Invalidate(Compose(executable, tier));
        }
    }

    public bool References(IconRequest request, string key) =>
        Matches(Key(request.CustomPath), key) || Matches(Key(request.ExecutablePath), key);

    // Completion keys carry a tier suffix; any tier of a referenced path interests the
    // listener, whose own read re-resolves the tier it currently renders at.
    private static bool Matches(string? path, string key) =>
        path != null && key.Length > path.Length && key[path.Length] == TierSeparator &&
        string.Compare(key, 0, path, 0, path.Length, StringComparison.OrdinalIgnoreCase) == 0;

    internal static int Quantize(double pixels)
    {
        if (double.IsNaN(pixels) || pixels <= 0) pixels = DefaultPixels;
        int requested = (int)Math.Ceiling(pixels);
        foreach (int tier in Tiers)
            if (requested <= tier) return tier;
        return Tiers[Tiers.Length - 1];
    }

    /// <summary>Request size for callers without a visual (scanner, startup prewarm), honoring the system DPI.</summary>
    internal static double SystemFallbackPixels => DefaultPixels * NativeMethods.GetDpiForSystem() / DotsPerInch;

    private static string Compose(string path, int tier) => path + TierSeparator + tier;

    private static void Decompose(string key, out string path, out int pixels)
    {
        int separator = key.LastIndexOf(TierSeparator);
        path = separator >= 0 ? key[..separator] : key;
        pixels = separator >= 0 && int.TryParse(key[(separator + 1)..], out int tier) ? tier : Quantize(DefaultPixels);
    }

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

    private static Asset? Load(string key, Asset? previous)
    {
        long start = Stopwatch.GetTimestamp();
        Decompose(key, out string path, out int pixels);
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists) return null;
            var stamp = file.LastWriteTimeUtc;
            long length = file.Length;
            long metadataDone = Stopwatch.GetTimestamp();
            if (previous != null && previous.Stamp == stamp && previous.Length == length)
                return previous;
            var image = IconHelper.LoadFile(path, pixels);
            if (Logger.IsVerboseEnabled)
                Logger.Info($"icon-load size={pixels} metadata={Stopwatch.GetElapsedTime(start, metadataDone).TotalMilliseconds:F1}ms " +
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
