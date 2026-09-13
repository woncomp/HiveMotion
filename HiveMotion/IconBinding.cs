using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace HiveMotion;

/// <summary>One background-priority dispatcher operation per batch of resource completions.</summary>
internal static class IconRefreshQueue
{
    private sealed class QueueState
    {
        public readonly HashSet<Action> Pending = new();
        public bool Scheduled;
    }
    private static readonly ConditionalWeakTable<Dispatcher, QueueState> Queues = new();

    public static void Enqueue(Dispatcher dispatcher, Action refresh)
    {
        var state = Queues.GetOrCreateValue(dispatcher);
        lock (state)
        {
            if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished) return;
            state.Pending.Add(refresh);
            if (state.Scheduled) return;
            state.Scheduled = true;
        }
        try
        {
            dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                Action[] work;
                lock (state)
                {
                    work = state.Pending.ToArray();
                    state.Pending.Clear();
                    state.Scheduled = false;
                }
                foreach (var action in work) action();
            }));
        }
        catch (InvalidOperationException) when (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            lock (state) { state.Pending.Clear(); state.Scheduled = false; }
        }
    }
}

/// <summary>Image-local identity and lifetime. Queued callbacks always read the current request.</summary>
internal static class IconBinding
{
    private static readonly ConditionalWeakTable<Image, Binding> Bindings = new();

    public static void Set(Image image, IconRequest request, Action<bool>? showFallback = null,
        ImageSource? fallback = null, IconService? service = null)
    {
        image.Dispatcher.VerifyAccess();
        var binding = Bindings.GetValue(image, value => new Binding(value, service ?? IconService.Shared));
        binding.Set(request, showFallback, fallback);
    }

    private sealed class Binding
    {
        private readonly Image _image;
        private readonly IconService _icons;
        private IconRequest _request = IconRequest.Empty;
        private Action<bool>? _showFallback;
        private ImageSource? _fallback;
        private volatile bool _active;
        private Window? _window;

        public Binding(Image image, IconService icons)
        {
            _image = image;
            _icons = icons;
            image.Loaded += (_, _) => Activate();
            image.Unloaded += (_, _) =>
            {
                _active = false;
                _icons.Changed -= OnChanged;
                if (_window != null)
                {
                    _window.DpiChanged -= OnDpiChanged;
                    _window = null;
                }
            };
        }

        public void Set(IconRequest request, Action<bool>? showFallback, ImageSource? fallback)
        {
            Volatile.Write(ref _request, request);
            _showFallback = showFallback;
            _fallback = fallback;
            Apply();
            if (_active || _image.IsLoaded)
            {
                Activate();
                _icons.Request(request, Pixels);
            }
        }

        private void Activate()
        {
            if (_active) return;
            _active = true;
            _icons.Changed += OnChanged;
            _window ??= Window.GetWindow(_image);
            if (_window != null)
                _window.DpiChanged += OnDpiChanged;
            Apply();
            _icons.Request(_request, Pixels);
        }

        // Rendered size × monitor DPI, resolved synchronously on the UI thread.
        // Unmeasured or not-yet-loaded images fall back to the default size so the
        // first apply is deterministic; the Loaded activation re-resolves at real DPI.
        private double Pixels
        {
            get
            {
                double dip = !double.IsNaN(_image.Width) && _image.Width > 0 ? _image.Width
                    : _image.ActualWidth > 0 ? _image.ActualWidth
                    : IconService.DefaultPixels;
                double scale = _image.IsLoaded ? VisualTreeHelper.GetDpi(_image).DpiScaleX : 1.0;
                return dip * scale;
            }
        }

        private void OnDpiChanged(object sender, DpiChangedEventArgs e) =>
            IconRefreshQueue.Enqueue(_image.Dispatcher, () =>
            {
                _icons.Request(Volatile.Read(ref _request), Pixels);
                Refresh();
            });

        private void OnChanged(string key)
        {
            if (_active && _icons.References(Volatile.Read(ref _request), key))
                IconRefreshQueue.Enqueue(_image.Dispatcher, Refresh);
        }

        private void Refresh()
        {
            if (_active) Apply();
        }

        private void Apply()
        {
            var image = _icons.TryGetCached(_request, Pixels) ?? _fallback;
            if (!ReferenceEquals(_image.Source, image)) _image.Source = image;
            _showFallback?.Invoke(image == null);
        }
    }
}
