using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using HiveMotion;

internal static class Program
{
    private sealed record Value(int Version);

    [STAThread]
    private static int Main()
    {
        var tests = new (string Name, Action Run)[]
        {
            ("Blocked extraction does not block cache reads, assignment, or initial visuals", InitialDisplay),
            ("Concurrent requests share one worker and retry only after the interval", Deduplication),
            ("Two workers bound concurrency and visible work precedes queued prewarm", Priority),
            ("Invalidation discards in-flight results without overlapping the same resource", Invalidation),
            ("Transient failure retains a usable resource and can recover", FailureRecovery),
            ("Disposal returns while extraction is blocked and suppresses publication", Disposal),
            ("Readiness requires both Show and rendering for the current presentation", Readiness),
            ("Completion cannot replace a newer cell occupying the same letter", ReplacedCell),
            ("Search transitions defer icons without changing query or result controls", SearchTransition),
            ("File replacement reloads resources and both image dimensions are bounded", FileVersions),
            ("Runtime icons remain usable while custom icons are loading", Selection),
            ("Editor bindings reject stale selection and unloaded-control completions", EditorBinding),
            ("Initially missing icons retry after the validation interval", InitialFailure),
            ("Custom and default executable icons share loading and invalidation", SharedExecutableResource),
            ("Size tiers cache the same file per size without duplicate same-tier loads", SizeTiers),
            ("Invalidation evicts every size tier of a request", TierInvalidation),
            ("Glyphs stay size-independent and never start file loads", GlyphTiers)
        };
        try
        {
            foreach (var test in tests)
            {
                test.Run();
                Console.WriteLine($"PASS {test.Name}");
            }
            Console.WriteLine($"{tests.Length} icon checks passed. No interactive windows were shown.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { Logger.Shutdown(); }
    }

    private static void InitialDisplay()
    {
        using var release = new ManualResetEventSlim();
        using var entered = new ManualResetEventSlim();
        var image = MakeImage(Colors.Red);
        int uiThread = Environment.CurrentManagedThreadId;
        int loaderThread = uiThread;
        using var service = new IconService((_, _) =>
        {
            loaderThread = Environment.CurrentManagedThreadId;
            entered.Set(); Wait(release); return image;
        });
        var app = new ApplicationMotion { Key = 'A', ExecutablePath = "blocked.exe", DisplayName = "Blocked" };
        var cells = new CellAssigner(new[] { app }).Assign(Array.Empty<RunningWindow>());
        Check(cells.Count == 1 && cells[0].Letter == 'A', "Assignment failed without icons.");
        Check(!entered.IsSet, "Assignment triggered the loader.");
        var grid = new TaskGridView(service);
        try
        {
            grid.BeginIconPresentation();
            grid.SetCells(cells);
            Wait(entered);
            Check(loaderThread != uiThread, "Extraction ran on the UI thread.");
            Check(CellImage(grid, 'A').Source == null, "Initial content waited for extraction.");
            Check(service.TryGetCached(cells[0].IconRequest, 48) == null, "Cache read fabricated a result.");
            release.Set();
            Pump(() => ReferenceEquals(service.TryGetCached(cells[0].IconRequest, 48), image));
            Drain();
            Check(CellImage(grid, 'A').Source == null, "Icon applied before presentation was ready.");
            grid.EnableIconUpdates();
            Pump(() => ReferenceEquals(CellImage(grid, 'A').Source, image));
            Check(cells[0].Icon == null, "View mutated the cell projection.");
        }
        finally { release.Set(); grid.ResetForOverlayClose(); }
    }

    private static void Deduplication()
    {
        using var release = new ManualResetEventSlim();
        using var entered = new ManualResetEventSlim();
        int calls = 0;
        using var cache = new AsyncResourceCache<Value>((_, _) =>
        {
            Interlocked.Increment(ref calls); entered.Set(); Wait(release); return new(1);
        });
        try
        {
            Parallel.For(0, 100, _ => cache.Request("same"));
            Wait(entered);
            Check(calls == 1 && cache.Read("same") == null, "Duplicate or blocking request.");
            release.Set();
            Pump(() => cache.Read("same") != null);
            for (int i = 0; i < 100; i++) cache.Request("same");
            Drain();
            Check(calls == 1, "Fresh cache hit reloaded the resource.");
        }
        finally { release.Set(); }
    }

    private static void Priority()
    {
        using var release = new ManualResetEventSlim();
        using var entered = new CountdownEvent(2);
        var order = new ConcurrentQueue<string>();
        int active = 0, peak = 0;
        using var cache = new AsyncResourceCache<Value>((key, _) =>
        {
            int count = Interlocked.Increment(ref active);
            int before;
            do { before = peak; } while (count > before && Interlocked.CompareExchange(ref peak, count, before) != before);
            order.Enqueue(key);
            if (key.StartsWith("block")) { entered.Signal(); Wait(release); }
            Interlocked.Decrement(ref active);
            return new(1);
        });
        try
        {
            cache.Request("block1"); cache.Request("block2");
            Check(entered.Wait(TimeSpan.FromSeconds(5)), "Workers did not start.");
            cache.Request("prewarm", visible: false);
            cache.Request("visible");
            Check(order.Count == 2, "A third loader started.");
            release.Set();
            Pump(() => cache.Read("prewarm") != null && cache.Read("visible") != null);
            Check(peak == 2, "Concurrency bound was exceeded.");
            // Selection happens under the cache lock. With two free workers execution can reorder,
            // so verify priority separately with one worker still blocked below.
        }
        finally { release.Set(); }

        using var first = new ManualResetEventSlim();
        using var second = new ManualResetEventSlim();
        using var both = new CountdownEvent(2);
        order.Clear();
        using var priorityCache = new AsyncResourceCache<Value>((key, _) =>
        {
            order.Enqueue(key);
            if (key == "one") { both.Signal(); Wait(first); }
            if (key == "two") { both.Signal(); Wait(second); }
            return new(1);
        });
        try
        {
            priorityCache.Request("one"); priorityCache.Request("two");
            Check(both.Wait(TimeSpan.FromSeconds(5)), "Workers did not start.");
            priorityCache.Request("background", false); priorityCache.Request("foreground");
            first.Set();
            Pump(() => priorityCache.Read("background") != null);
            Check(order.ToArray()[2] == "foreground", "Prewarm overtook visible work.");
        }
        finally { first.Set(); second.Set(); }
    }

    private static void Invalidation()
    {
        using var release = new ManualResetEventSlim();
        using var entered = new ManualResetEventSlim();
        int calls = 0;
        var published = new ConcurrentQueue<int>();
        using var cache = new AsyncResourceCache<Value>((_, _) =>
        {
            int call = Interlocked.Increment(ref calls);
            if (call == 1) { entered.Set(); Wait(release); }
            return new(call);
        });
        cache.Changed += key => published.Enqueue(cache.Read(key)!.Version);
        try
        {
            cache.Request("icon"); Wait(entered);
            cache.Invalidate("icon"); cache.Request("icon");
            Check(calls == 1, "Invalidation overlapped an in-flight load.");
            release.Set();
            Pump(() => cache.Read("icon")?.Version == 2 && published.Count > 0);
            Check(published.All(v => v == 2), "Stale resource version was published.");
        }
        finally { release.Set(); }
    }

    private static void FailureRecovery()
    {
        int calls = 0;
        var now = DateTimeOffset.UtcNow;
        using var cache = new AsyncResourceCache<Value>((_, _) =>
        {
            int call = Interlocked.Increment(ref calls);
            return call == 2 ? null : new(call);
        }, () => now);
        cache.Request("icon"); Pump(() => cache.Read("icon") != null);
        now += TimeSpan.FromSeconds(31);
        cache.Request("icon"); Pump(() => Volatile.Read(ref calls) == 2);
        Check(cache.Read("icon")?.Version == 1, "Transient failure removed the last image.");
        // Explicit invalidation also safely orders a request arriving during completion.
        cache.Invalidate("icon");
        Pump(() => cache.Read("icon")?.Version == 3);
    }

    private static void Disposal()
    {
        using var release = new ManualResetEventSlim();
        using var entered = new ManualResetEventSlim();
        using var finished = new ManualResetEventSlim();
        int published = 0;
        var cache = new AsyncResourceCache<Value>((_, _) =>
        {
            entered.Set(); Wait(release); finished.Set(); return new(1);
        });
        cache.Changed += _ => Interlocked.Increment(ref published);
        try
        {
            cache.Request("icon"); Wait(entered);
            cache.Dispose();
            Check(!finished.IsSet, "Disposal waited for the blocked loader.");
            release.Set(); Wait(finished); Drain();
            Check(published == 0, "Disposed consumer received a publication.");
        }
        finally { release.Set(); cache.Dispose(); }
    }

    private static void Readiness()
    {
        var state = new IconPresentationState();
        state.Begin(); int old = state.Generation;
        state.Rendered(old); Check(!state.CanApply, "Rendering inside Show enabled updates too early.");
        state.Shown(old); Check(state.CanApply, "Ready view cannot apply icons.");
        state.Transitioning = true; Check(!state.CanApply, "Animation did not defer updates.");
        state.Close(); state.Begin();
        state.Shown(old); state.Rendered(old);
        Check(!state.CanApply, "Old activation enabled a new presentation.");
    }

    private static void ReplacedCell()
    {
        using var release = new ManualResetEventSlim();
        using var entered = new ManualResetEventSlim();
        var oldImage = MakeImage(Colors.Red); var currentImage = MakeImage(Colors.Blue);
        using var service = new IconService((path, _) =>
        {
            if (path.EndsWith("old.png")) { entered.Set(); Wait(release); return oldImage; }
            return currentImage;
        });
        var grid = new TaskGridView(service);
        try
        {
            grid.BeginIconPresentation(); grid.SetCells(new[] { Cell("old.png") }); grid.EnableIconUpdates();
            Wait(entered);
            grid.ResetForOverlayClose(); grid.BeginIconPresentation();
            var latest = Cell("new.png");
            grid.SetCells(new[] { latest }); grid.EnableIconUpdates();
            Pump(() => ReferenceEquals(CellImage(grid, 'A').Source, currentImage));
            release.Set();
            Pump(() => ReferenceEquals(service.TryGetCached(Cell("old.png").IconRequest, 48), oldImage));
            Drain();
            Check(ReferenceEquals(CellImage(grid, 'A').Source, currentImage), "Old cell overwrote current icon.");
            Check(latest.Icon == null, "Icon callback mutated a cell model.");
        }
        finally { release.Set(); grid.ResetForOverlayClose(); }
    }

    private static void SearchTransition()
    {
        using var release = new ManualResetEventSlim();
        var image = MakeImage(Colors.Green);
        using var service = new IconService((_, _) => { Wait(release); return image; });
        var grid = new TaskGridView(service);
        try
        {
            grid.BeginIconPresentation(); grid.SetCells(new[] { Cell("search.png") }); grid.EnableIconUpdates();
            grid.EnterSearch();
            var input = (TextBox)grid.FindName("SearchInput"); input.Text = "Alpha";
            var list = (StackPanel)grid.FindName("ResultList"); var row = list.Children[0];
            release.Set();
            Pump(() => service.TryGetCached(Cell("search.png").IconRequest, 48) != null);
            Drain();
            Check(CellImage(grid, 'A').Source == null, "Moving cells accepted an icon update.");
            Pump(() => ReferenceEquals(CellImage(grid, 'A').Source, image));
            Check(grid.Searching && input.Text == "Alpha" && ReferenceEquals(row, list.Children[0]),
                "Icon completion reset search or rebuilt its controls.");
            // Rows render at 24 DIP (tier 32 at 96 DPI); wait for that tier before asserting.
            Pump(() => service.TryGetCached(Cell("search.png").IconRequest, 24) != null);
            Drain();
            Check(Descendants<Image>(row).Any(i => ReferenceEquals(i.Source, image)), "Search row icon was not updated.");
        }
        finally { release.Set(); grid.ResetForOverlayClose(); }
    }

    private static void FileVersions()
    {
        string directory = Path.Combine(Path.GetTempPath(), "HiveMotion.IconChecks-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "portrait.png");
        using var service = new IconService();
        try
        {
            WritePng(path, 8, 400, Colors.Red);
            var request = new IconRequest(CustomPath: path);
            service.Request(request, 48); Pump(() => service.TryGetCached(request, 48) != null);
            var first = (BitmapSource)service.TryGetCached(request, 48)!;
            Check(first.IsFrozen && first.PixelWidth <= 48 && first.PixelHeight <= 48, "Image dimensions are unbounded.");
            WritePng(path, 400, 8, Colors.Blue);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(3));
            service.Invalidate(request); service.Request(request, 48);
            Pump(() => !ReferenceEquals(service.TryGetCached(request, 48), first));
            var second = (BitmapSource)service.TryGetCached(request, 48)!;
            Check(second.PixelWidth > second.PixelHeight && second.PixelWidth <= 48, "Replacement returned stale content.");
        }
        finally
        {
            // Exact files created by this test only; no recursive deletion.
            File.Delete(path); Directory.Delete(directory);
        }
    }

    private static void Selection()
    {
        using var release = new ManualResetEventSlim();
        var runtime = MakeImage(Colors.Blue); var custom = MakeImage(Colors.Red);
        using var service = new IconService((_, _) => { Wait(release); return custom; });
        var request = new IconRequest(CustomPath: "custom.png");
        try
        {
            Check(ReferenceEquals(service.TryGetCached(request, 48, runtime), runtime), "Available runtime icon was blanked.");
            service.Request(request, 48); release.Set();
            Pump(() => ReferenceEquals(service.TryGetCached(request, 48, runtime), custom));
        }
        finally { release.Set(); }
    }

    private static void EditorBinding()
    {
        using var releaseOld = new ManualResetEventSlim();
        using var releaseClosing = new ManualResetEventSlim();
        using var oldEntered = new ManualResetEventSlim();
        using var closingEntered = new ManualResetEventSlim();
        var old = MakeImage(Colors.Red); var current = MakeImage(Colors.Blue);
        using var service = new IconService((path, _) =>
        {
            if (path.EndsWith("old.png")) { oldEntered.Set(); Wait(releaseOld); return old; }
            if (path.EndsWith("closing.png")) { closingEntered.Set(); Wait(releaseClosing); return old; }
            return current;
        });
        var image = new Image();
        var fallback = new TextBlock();
        void Bind(string path) => IconBinding.Set(image, new(CustomPath: path), missing =>
            fallback.Visibility = missing ? Visibility.Visible : Visibility.Collapsed, service: service);
        try
        {
            Bind("old.png");
            // Exercise WPF's lifetime handlers without creating or showing a native window.
            image.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            Wait(oldEntered);
            Bind("new.png");
            Pump(() => ReferenceEquals(image.Source, current));
            releaseOld.Set();
            Pump(() => ReferenceEquals(service.TryGetCached(new(CustomPath: "old.png"), 48), old));
            Drain();
            Check(ReferenceEquals(image.Source, current) && fallback.Visibility == Visibility.Collapsed,
                "Old editor selection overwrote the selected image.");
            Bind("closing.png"); Wait(closingEntered);
            image.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
            releaseClosing.Set();
            Pump(() => ReferenceEquals(service.TryGetCached(new(CustomPath: "closing.png"), 48), old));
            Drain();
            Check(image.Source == null, "Unloaded control received a resource update.");
            image.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            Check(ReferenceEquals(image.Source, old), "Reloaded control failed to consume cached completion.");
        }
        finally
        {
            releaseOld.Set(); releaseClosing.Set();
            image.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        }
    }

    private static void InitialFailure()
    {
        int calls = 0;
        long ticks = DateTimeOffset.UtcNow.UtcTicks;
        using var finished = new ManualResetEventSlim();
        using var cache = new AsyncResourceCache<Value>((_, _) =>
        {
            int call = Interlocked.Increment(ref calls);
            finished.Set();
            return call == 1 ? null : new(call);
        }, () => new DateTimeOffset(Interlocked.Read(ref ticks), TimeSpan.Zero));
        cache.Request("missing"); Wait(finished);
        // Queue another resource to prove the first request's completion has been processed.
        cache.Request("barrier"); Pump(() => cache.Read("barrier") != null);
        cache.Request("missing"); Drain();
        Check(cache.Read("missing") == null && calls == 2, "Failure retried without backoff.");
        Interlocked.Add(ref ticks, TimeSpan.FromSeconds(31).Ticks);
        cache.Request("missing"); Pump(() => cache.Read("missing") != null);
        Check(cache.Read("missing")!.Version == 3, "Missing resource did not recover after backoff.");
    }

    private static void SharedExecutableResource()
    {
        int calls = 0;
        var first = MakeImage(Colors.Red); var second = MakeImage(Colors.Blue);
        using var service = new IconService((_, _) => Interlocked.Increment(ref calls) == 1 ? first : second);
        var custom = new IconRequest(CustomPath: "shared.exe");
        var executable = new IconRequest(ExecutablePath: "SHARED.EXE");
        service.Request(custom, 48); service.Request(executable, 48);
        Pump(() => ReferenceEquals(service.TryGetCached(executable, 48), first));
        Check(calls == 1 && ReferenceEquals(service.TryGetCached(custom, 48), first), "Executable resources used separate caches.");
        service.Invalidate(custom); service.Request(executable, 48);
        Pump(() => ReferenceEquals(service.TryGetCached(custom, 48), second));
        Check(calls == 2 && ReferenceEquals(service.TryGetCached(executable, 48), second), "Executable alias retained stale content.");
    }

    private static void SizeTiers()
    {
        var calls = new ConcurrentQueue<int>();
        var byTier = new Dictionary<int, ImageSource>
        {
            [32] = MakeImage(Colors.Red), [48] = MakeImage(Colors.Green), [96] = MakeImage(Colors.Blue)
        };
        using var service = new IconService((_, pixels) => { calls.Enqueue(pixels); return byTier[pixels]; });
        var request = new IconRequest(CustomPath: "tiers.png");
        Check(IconService.Quantize(24) == 32 && IconService.Quantize(48) == 48 &&
              IconService.Quantize(96) == 96 && IconService.Quantize(400) == 96,
              "Tier quantization escaped the bounded set.");
        service.Request(request, 24); // tier 32
        service.Request(request, 40); // tier 48
        service.Request(request, 96); // tier 96
        Pump(() => calls.Count >= 3);
        Drain();
        Check(calls.OrderBy(p => p).SequenceEqual(new[] { 32, 48, 96 }), "Loads did not follow the quantized tiers.");
        Check(ReferenceEquals(service.TryGetCached(request, 24), byTier[32]) &&
              ReferenceEquals(service.TryGetCached(request, 40), byTier[48]) &&
              ReferenceEquals(service.TryGetCached(request, 96), byTier[96]),
              "Tiers did not resolve to their own assets.");
        // The same file at already-loaded tiers must not start duplicate loads.
        service.Request(request, 30); service.Request(request, 33); service.Request(request, 96);
        Drain();
        Check(calls.Count == 3, "Repeated same-tier requests started duplicate loads.");
    }

    private static void TierInvalidation()
    {
        int calls = 0;
        using var service = new IconService((_, _) => { Interlocked.Increment(ref calls); return MakeImage(Colors.Red); });
        var request = new IconRequest(CustomPath: "invalidate-tiers.png");
        service.Request(request, 24); service.Request(request, 96);
        Pump(() => Volatile.Read(ref calls) == 2);
        // A single Invalidate re-requests every tier that has an entry (32 and 96 here).
        service.Invalidate(request);
        Pump(() => Volatile.Read(ref calls) == 4);
        // The never-loaded middle tier has no entry to evict and must load on request.
        service.Request(request, 40);
        Pump(() => service.TryGetCached(request, 40) != null);
        Drain();
        Check(Volatile.Read(ref calls) == 5, "Unexpected load count after invalidation.");
        Check(service.TryGetCached(request, 24) != null &&
              service.TryGetCached(request, 96) != null,
              "Invalidation left a tier unloaded.");
    }

    private static void GlyphTiers()
    {
        int calls = 0;
        using var service = new IconService((_, _) => { Interlocked.Increment(ref calls); return MakeImage(Colors.Red); });
        service.PrepareGlyphs();
        var request = new IconRequest(Glyph: "\uE71D");
        var first = service.TryGetCached(request, 24);
        Check(first != null, "Glyph lookup returned nothing.");
        Check(ReferenceEquals(service.TryGetCached(request, 24), first) &&
              ReferenceEquals(service.TryGetCached(request, 96), first),
              "Glyph result changed with the requested size.");
        service.Request(request, 96);
        Drain();
        Check(calls == 0, "Glyph request started a file load.");
    }

    private static HiveCell Cell(string path) => new()
    {
        Letter = 'A', AppName = "Alpha", Title = "Alpha", IconRequest = new(CustomPath: path)
    };

    private static Image CellImage(TaskGridView grid, char letter) =>
        (Image)((Canvas)grid.FindName("HexCanvas")).Children.OfType<HiveCellView>()
            .Single(v => v.PoolLetter == letter).FindName("AppIcon");

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T value) yield return value;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    private static ImageSource MakeImage(Color color)
    {
        var image = new DrawingImage(new GeometryDrawing(new SolidColorBrush(color), null,
            new RectangleGeometry(new Rect(0, 0, 8, 8))));
        image.Freeze(); return image;
    }

    private static void WritePng(string path, int width, int height, Color color)
    {
        var pixels = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        { pixels[i] = color.B; pixels[i + 1] = color.G; pixels[i + 2] = color.R; pixels[i + 3] = 255; }
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }

    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

    private static void Wait(ManualResetEventSlim signal) =>
        Check(signal.Wait(TimeSpan.FromSeconds(5)), "Timed out waiting for controlled worker.");

    private static void Drain()
    {
        bool done = false;
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => done = true));
        Pump(() => done);
    }

    private static void Pump(Func<bool> condition)
    {
        if (condition()) return;
        var frame = new DispatcherFrame();
        var clock = Stopwatch.StartNew();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(2) };
        timer.Tick += (_, _) => { if (condition() || clock.Elapsed > TimeSpan.FromSeconds(5)) frame.Continue = false; };
        timer.Start();
        try { Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); }
        Check(condition(), "Timed out waiting for UI/resource completion.");
    }
}
