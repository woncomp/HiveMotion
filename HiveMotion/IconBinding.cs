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

        public Binding(Image image, IconService icons)
        {
            _image = image;
            _icons = icons;
            image.Loaded += (_, _) => Activate();
            image.Unloaded += (_, _) =>
            {
                _active = false;
                _icons.Changed -= OnChanged;
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
                _icons.Request(request);
            }
        }

        private void Activate()
        {
            if (_active) return;
            _active = true;
            _icons.Changed += OnChanged;
            Apply();
            _icons.Request(_request);
        }

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
            var image = _icons.TryGetCached(_request) ?? _fallback;
            if (!ReferenceEquals(_image.Source, image)) _image.Source = image;
            _showFallback?.Invoke(image == null);
        }
    }
}
