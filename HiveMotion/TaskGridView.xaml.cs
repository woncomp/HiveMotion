using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using Cursors = System.Windows.Input.Cursors;
using HiveMotion.Localization;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Image = System.Windows.Controls.Image;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Orientation = System.Windows.Controls.Orientation;
using Point = System.Windows.Point;

namespace HiveMotion;

public enum GridLayerKind
{
    Home,
    Folder,
    WindowView
}

public partial class TaskGridView : System.Windows.Controls.UserControl
{
    private readonly List<HiveCellView> _cellViews = new();
    private readonly List<SearchResultVisual> _itemVisuals = new();
    private IReadOnlyList<HiveCell> _cells = Array.Empty<HiveCell>();
    private List<HiveCell> _results = new();
    private string _query = string.Empty;
    private int _highlight;
    private bool _searching;
    private bool _previewVisible;
    private PreviewMode _previewMode = PreviewMode.None;
    private double _previewMaxH = 320;
    private HiveCell? _hoveredCell;
    private Action? _confirmAction;
    private bool _mouseArmed = true;
    private NativeMethods.POINT _mouseAnchor;
    private readonly DwmThumbnailPreview _dwmPreview = new();

    private readonly IconService _icons;
    private readonly IconPresentationState _iconPresentation = new();
    private readonly Dictionary<char, HiveCell> _displayedCells = new();
    /// <summary>Content last written into each pool view; the diff baseline for skipping no-op updates.</summary>
    private readonly Dictionary<char, HiveCell> _appliedCells = new();
    /// <summary>Bumped on every ApplyCells; the result list is stale when its build version lags.</summary>
    private int _cellsVersion;
    private int _resultsVersion = -1;
    private IReadOnlyList<HiveCell>? _pendingCells;
    private System.Windows.Threading.DispatcherTimer? _transitionTimer;
    private int _transitionGeneration;
    private SearchTransitionState _transitionState;

    /// <summary>Physical pixels the cursor must travel from its show-time anchor before it re-arms.</summary>
    private const int MouseWakeThreshold = 6;
    private static readonly SolidColorBrush ActiveStatusBrush = FrozenBrush("#D9FFD97A");
    private static readonly SolidColorBrush InactiveStatusBrush = FrozenBrush("#4DFFFFFF");
    private static readonly SolidColorBrush HighlightRowBrush = FrozenBrush("#1FF5B301");
    private static readonly Color SpaceBarActiveColor = (Color)ColorConverter.ConvertFromString("#A6F5B301");
    private static readonly Color SpaceBarIdleColor = (Color)ColorConverter.ConvertFromString("#33FFFFFF");

    private enum PreviewMode
    {
        None,
        Thumbnail,
        LaunchInfo
    }

    private enum SearchTransitionState
    {
        Overview,
        Entering,
        Search,
        Exiting
    }

    private sealed class SearchResultVisual
    {
        public Border Root { get; init; } = null!;
        public Border Bar { get; init; } = null!;
        public Image IconImage { get; init; } = null!;
        public TextBlock FallbackIcon { get; init; } = null!;
        public TextBlock Title { get; init; } = null!;
        public TextBlock Subtitle { get; init; } = null!;
        public Border StatusDot { get; init; } = null!;
        public TextBlock StatusText { get; init; } = null!;
        public HiveCell? Cell { get; set; }
    }

    /// <summary>HWND of the owning overlay window; the DWM thumbnail draws into it.</summary>
    public IntPtr OverlayHwnd { get; set; }

    public event EventHandler<HiveCell>? CellChosen;
    public event EventHandler? CloseRequested;
    /// <summary>Backspace on the grid: pop one layer (folder → home).</summary>
    public event EventHandler? BackRequested;
    /// <summary>Ctrl+P over a cell (grid hover or search highlight): pin or unpin it.</summary>
    public event EventHandler<HiveCell>? PinToggleRequested;
    /// <summary>Ctrl+R on the search-list highlight: open the app's file location.</summary>
    public event EventHandler<HiveCell>? RevealRequested;
    /// <summary>Ctrl+S on the search-list highlight: copy the full command line.</summary>
    public event EventHandler<HiveCell>? CopyCommandRequested;

    public TaskGridView() : this(IconService.Shared) { }

    internal TaskGridView(IconService icons)
    {
        _icons = icons;
        InitializeComponent();
        // Search typing keeps full IME support even though the overlay window
        // disables it; this local value wins over the inherited one.
        InputMethod.SetIsInputMethodEnabled(SearchInput, true);
        ApplyLocalizedStrings();
        // Lives for the whole app lifetime, so no unsubscribe is needed.
        LocalizationManager.Instance.CultureChanged += (_, _) => ApplyLocalizedStrings();
        PreviewMouseMove += (_, _) => WakeMouseIfMoved();
        // While disarmed, swallow every mouse button press: no cell clicks, no backdrop
        // dismiss, no focus shifts — the user must move the mouse first.
        PreviewMouseDown += (_, e) =>
        {
            if (!_mouseArmed)
                e.Handled = true;
        };
        CreateCellPool();
        CreateResultPool();
    }

    internal void BeginIconPresentation()
    {
        _icons.Changed -= OnIconChanged;
        _iconPresentation.Begin();
        _icons.Changed += OnIconChanged;
    }

    internal void EnableIconUpdates()
    {
        int generation = _iconPresentation.Generation;
        _iconPresentation.Shown(generation);
        _iconPresentation.Rendered(generation);
        IconRefreshQueue.Enqueue(Dispatcher, RefreshIcons);
    }

    private void OnIconChanged(string key) => IconRefreshQueue.Enqueue(Dispatcher, RefreshIcons);

    private ImageSource? ResolveIcon(HiveCell cell) =>
        _icons.TryGetCached(cell.IconRequest, cell.Icon);

    private void RefreshIcons()
    {
        _iconPresentation.Transitioning = _transitionState is SearchTransitionState.Entering or SearchTransitionState.Exiting;
        if (!_iconPresentation.CanApply) return;
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        foreach (var view in _cellViews)
            if (_displayedCells.TryGetValue(view.PoolLetter, out var cell))
                view.UpdateIcon(ResolveIcon(cell));
        foreach (var visual in _itemVisuals)
            if (visual.Cell is { } cell) UpdateResultIcon(visual, cell);
        if (Logger.IsVerboseEnabled)
            Logger.Info($"icon-view-update {System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds:F1}ms thread={Environment.CurrentManagedThreadId}");
    }

    public bool Searching => _searching;

    /// <summary>Clears transient UI state while the overlay is hidden so every open starts in overview.</summary>
    public void ResetForOverlayClose()
    {
        _iconPresentation.Close();
        _icons.Changed -= OnIconChanged;
        _displayedCells.Clear();
        // _appliedCells intentionally survives the close: it is the diff baseline that lets
        // the next open skip SetCell for cells whose content did not change while hidden.
        _pendingCells = null;
        HideConfirm();
        ResetPreview();
        ExitSearchImmediate();
    }

    /// <summary>
    /// Called each time the overlay appears: hides the cursor and suspends hover and
    /// clicks until the user physically moves the mouse past MouseWakeThreshold.
    /// Prevents the cell that happens to sit under the cursor from being hovered.
    /// </summary>
    public void DisarmMouse()
    {
        _mouseArmed = false;
        if (NativeMethods.GetCursorPos(out var point))
            _mouseAnchor = point;
        Mouse.OverrideCursor = Cursors.None;
        HexCanvas.IsHitTestVisible = false;
    }

    /// <summary>Restores the cursor and hit testing; also called defensively on hide.</summary>
    public void ArmMouse()
    {
        if (_mouseArmed)
            return;
        _mouseArmed = true;
        Mouse.OverrideCursor = null;
        HexCanvas.IsHitTestVisible = true;
    }

    private void WakeMouseIfMoved()
    {
        if (_mouseArmed || !NativeMethods.GetCursorPos(out var point))
            return;
        // Threshold in physical pixels guards against spurious WM_MOUSEMOVE (activation,
        // high-polling mouse jitter) that would otherwise unlock the grid instantly.
        if (Math.Abs(point.x - _mouseAnchor.x) < MouseWakeThreshold &&
            Math.Abs(point.y - _mouseAnchor.y) < MouseWakeThreshold)
            return;
        ArmMouse();
    }

    private GridLayerKind _activeLayerKind;
    private string? _activeLayerName;

    private void ApplyLocalizedStrings()
    {
        UpdateEscHint();
        if (_searching)
            RebuildResults();
        if (_previewMode == PreviewMode.LaunchInfo && _hoveredCell != null)
            ShowPreview(_hoveredCell);
    }

    /// <summary>Switches the grid chrome and shortcuts for the active overlay layer.</summary>
    public void SetActiveLayer(GridLayerKind kind, string? layerName)
    {
        _activeLayerKind = kind;
        _activeLayerName = layerName;
        PinHintPanel.Visibility = kind == GridLayerKind.Home ? Visibility.Visible : Visibility.Collapsed;
        UpdateEscHint();
        UpdateOverviewEmptyState();
    }

    private void UpdateEscHint()
    {
        EscHintText.Text = _searching
            ? Loc.Get("Grid_HintExitSearch")
            : _activeLayerKind != GridLayerKind.Home
                ? Loc.Format("Grid_HintBack", _activeLayerName ?? Loc.Get("Motion_WindowViewName"))
                : Loc.Get("Grid_HintClose");
    }

    public void SetBackdrop(System.Windows.Media.ImageSource? backdrop)
    {
        BackdropImage.Source = backdrop;
    }

    public void SetCells(IReadOnlyList<HiveCell> cells)
    {
        if (_transitionState is SearchTransitionState.Entering or SearchTransitionState.Exiting)
        {
            // The scanner refreshes shortly after the overlay opens. Applying it while
            // cached surfaces are moving invalidates every cache and visibly interrupts
            // the transition, so retain only the newest update until it is stable.
            _pendingCells = cells;
            return;
        }

        ApplyCells(cells, resetSearch: !_searching);
    }

    private void ApplyCells(IReadOnlyList<HiveCell> cells, bool resetSearch)
    {
        _cells = cells;
        _cellsVersion++;
        _resultsVersion = -1;
        _iconPresentation.ReplaceContent();
        _displayedCells.Clear();
        foreach (var cell in cells)
        {
            _displayedCells[cell.Letter] = cell;
            _icons.Request(cell.IconRequest);
        }
        UpdateOverviewEmptyState();
        _hoveredCell = null;
        if (ConfirmVisible)
            HideConfirm();
        var byLetter = cells.ToDictionary(cell => cell.Letter);
        foreach (var view in _cellViews)
        {
            char letter = view.PoolLetter;
            if (!byLetter.TryGetValue(letter, out var cell))
            {
                if (view.Visibility != Visibility.Collapsed)
                    view.Visibility = Visibility.Collapsed;
                _appliedCells.Remove(letter);
                continue;
            }
            // Unchanged content: skip SetCell entirely so reopening the overlay reuses the
            // existing visuals instead of invalidating text, badges, and icons per cell.
            if (_appliedCells.TryGetValue(letter, out var applied) && SameCellContent(applied, cell))
                continue;
            view.SetCell(cell, ResolveIcon(cell));
            _appliedCells[letter] = cell;
            if (view.Visibility != Visibility.Visible)
                view.Visibility = Visibility.Visible;
        }

        // The reset is skipped only when it is a verified no-op (pristine overview re-entry);
        // any live transient state takes the full path so behavior stays identical.
        if (resetSearch && !IsTransientStateClean())
        {
            _previewVisible = false;
            _previewMode = PreviewMode.None;
            _dwmPreview.Hide();
            HoverPreview.BeginAnimation(UIElement.OpacityProperty, null);
            HoverPreview.Opacity = 0;
            CopyToast.BeginAnimation(UIElement.OpacityProperty, null);
            CopyToast.Opacity = 0;
            ExitSearchImmediate();
        }

        // The result tree is built at ContextIdle after the opening frames, not between
        // hotkey receipt and Show(); EnterSearch forces a build, so the first Space press
        // always sees rows even if the deferred callback has not run yet.
        int generation = _cellsVersion;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ContextIdle, new Action(() =>
        {
            if (generation == _cellsVersion && !_searching && IsVisible)
                EnsureResultsBuilt();
        }));
    }

    /// <summary>Identity-relevant content for diffing; anything the view renders must be compared.</summary>
    private static bool SameCellContent(HiveCell? applied, HiveCell cell) =>
        applied != null &&
        ReferenceEquals(applied.Motion, cell.Motion) &&
        ReferenceEquals(applied.Icon, cell.Icon) &&
        applied.IconRequest == cell.IconRequest &&
        applied.WindowHandle == cell.WindowHandle &&
        applied.ProcessId == cell.ProcessId &&
        applied.ProcessCreationFileTime == cell.ProcessCreationFileTime &&
        applied.IsRunning == cell.IsRunning &&
        applied.Title == cell.Title &&
        applied.AppName == cell.AppName &&
        applied.ProcessName == cell.ProcessName &&
        applied.ExecutablePath == cell.ExecutablePath &&
        applied.CommandLineArguments == cell.CommandLineArguments &&
        applied.Motion?.IsConfigured == cell.Motion?.IsConfigured &&
        applied.Motion?.DisplayName == cell.Motion?.DisplayName &&
        applied.Motion?.IconPath == cell.Motion?.IconPath;

    /// <summary>
    /// True when the ApplyCells reset block would write only values the controls already
    /// hold — re-entering a pristine overview after the close-time cleanup. Any residue
    /// (search transition, preview, confirm, toast, running animation) makes this false so
    /// the full reset runs and behavior is unchanged.
    /// </summary>
    private bool IsTransientStateClean() =>
        !_searching &&
        _transitionState == SearchTransitionState.Overview &&
        !_previewVisible &&
        _previewMode == PreviewMode.None &&
        !ConfirmVisible &&
        HoverPreview.Opacity == 0 &&
        CopyToast.Opacity == 0 &&
        BarSearch.Visibility == Visibility.Collapsed &&
        ResultPanel.Opacity == 0;

    private void UpdateOverviewEmptyState()
    {
        OverviewEmpty.Visibility = _activeLayerKind == GridLayerKind.WindowView &&
                                   !_searching && _cells.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    /// <summary>Creates the fixed A-Z visual pool once; reopening only swaps model content.</summary>
    private void CreateCellPool()
    {
        for (char letter = 'A'; letter <= 'Z'; letter++)
        {
            var view = new HiveCellView { PoolLetter = letter, Visibility = Visibility.Collapsed };
            view.Clicked += (_, chosen) => CellChosen?.Invoke(this, chosen);
            view.Hovered += (_, hovered) =>
            {
                _hoveredCell = hovered;
                ShowPreview(hovered);
            };
            view.Unhovered += (_, unhovered) =>
            {
                if (_hoveredCell == unhovered)
                    _hoveredCell = null;
                HidePreview();
            };
            var center = KeyGrid.CenterOf(letter);
            Canvas.SetLeft(view, center.X - KeyGrid.HexW / 2);
            Canvas.SetTop(view, center.Y - KeyGrid.HexH / 2);
            HexCanvas.Children.Add(view);
            _cellViews.Add(view);
        }
    }

    public void EnterSearch()
    {
        if (_searching)
            return;
        _searching = true;
        UpdateOverviewEmptyState();
        _transitionState = SearchTransitionState.Entering;
        EnsureResultsBuilt();
        HidePreview();

        BarIdle.Visibility = Visibility.Collapsed;
        BarSearch.Visibility = Visibility.Visible;
        SpaceBarBorderBrush.Color = SpaceBarActiveColor;
        SpaceBarRidge.Opacity = 1;
        UpdateEscHint();

        SearchInput.Text = string.Empty;
        ResultPanel.Visibility = Visibility.Visible;
        ResultPanel.IsHitTestVisible = false;
        SplineAnimate(ResultPanel, UIElement.OpacityProperty, 1, 400);
        SplineAnimate(ResultPanelSlide, TranslateTransform.YProperty, 0, 400);

        foreach (var view in _cellViews)
        {
            view.IsHitTestVisible = false;
            view.SetSearching(true);
        }

        FocusSearchAfterFirstRender();
        ScheduleTransitionCompletion(620, SearchTransitionState.Search);
    }

    public void ExitSearch()
    {
        if (!_searching)
            return;
        _searching = false;
        UpdateOverviewEmptyState();
        _transitionState = SearchTransitionState.Exiting;

        // Return keyboard focus to the window itself: with null focus (ClearFocus)
        // no routed key events fire at all and the grid hotkeys go dead.
        var window = Window.GetWindow(this);
        if (window != null)
            Keyboard.Focus(window);

        SearchInput.Text = string.Empty;
        BarSearch.Visibility = Visibility.Collapsed;
        BarIdle.Visibility = Visibility.Visible;
        SpaceBarBorderBrush.Color = SpaceBarIdleColor;
        SpaceBarRidge.Opacity = 0.6;
        UpdateEscHint();

        SplineAnimate(ResultPanel, UIElement.OpacityProperty, 0, 300);
        SplineAnimate(ResultPanelSlide, TranslateTransform.YProperty, 24, 300);
        ResultPanel.IsHitTestVisible = false;

        foreach (var view in _cellViews)
        {
            view.IsHitTestVisible = true;
            view.SetSearching(false);
        }
        ScheduleTransitionCompletion(620, SearchTransitionState.Overview);
    }

    private void ExitSearchImmediate()
    {
        _searching = false;
        UpdateOverviewEmptyState();
        _transitionGeneration++;
        _transitionTimer?.Stop();
        _transitionState = SearchTransitionState.Overview;
        _query = string.Empty;
        SearchInput.Text = string.Empty;
        BarSearch.Visibility = Visibility.Collapsed;
        BarIdle.Visibility = Visibility.Visible;
        SpaceBarBorderBrush.Color = SpaceBarIdleColor;
        SpaceBarRidge.Opacity = 0.6;
        UpdateEscHint();
        ResultPanel.BeginAnimation(UIElement.OpacityProperty, null);
        ResultPanel.Opacity = 0;
        ResultPanelSlide.BeginAnimation(TranslateTransform.YProperty, null);
        ResultPanelSlide.Y = 24;
        ResultPanel.Visibility = Visibility.Visible;
        ResultPanel.IsHitTestVisible = false;
        foreach (var view in _cellViews)
        {
            view.IsHitTestVisible = true;
            view.ResetSearchTransforms();
        }
    }

    private void ScheduleTransitionCompletion(int durationMilliseconds, SearchTransitionState completedState)
    {
        _transitionTimer?.Stop();
        int generation = ++_transitionGeneration;
        var timer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(durationMilliseconds)
        };
        _transitionTimer = timer;
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (generation != _transitionGeneration)
                return;

            _transitionState = completedState;
            bool searchIsReady = completedState == SearchTransitionState.Search;
            ResultPanel.IsHitTestVisible = searchIsReady;
            ApplyPendingCells(resetSearch: !searchIsReady);
            RefreshIcons();
        };
        timer.Start();
    }

    private void ApplyPendingCells(bool resetSearch)
    {
        if (_pendingCells == null)
            return;

        var cells = _pendingCells;
        _pendingCells = null;
        ApplyCells(cells, resetSearch);
    }

    private void FocusSearchAfterFirstRender()
    {
        EventHandler? rendered = null;
        rendered = (_, _) =>
        {
            CompositionTarget.Rendering -= rendered;
            if (!_searching || _transitionState != SearchTransitionState.Entering)
                return;

            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, new Action(() =>
            {
                if (_searching && _transitionState == SearchTransitionState.Entering)
                    SearchInput.Focus();
            }));
        };
        CompositionTarget.Rendering += rendered;
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        _query = SearchInput.Text;
        QueryPlaceholder.Visibility = _query.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_searching)
            RebuildResults();
    }

    private void OnSearchInputPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // A confirm dialog owns all keys while visible.
        if (ConfirmVisible)
        {
            if (e.Key == Key.Enter)
                CommitConfirm();
            else if (e.Key == Key.Escape)
                HideConfirm();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.P && Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (_activeLayerKind == GridLayerKind.Home && HighlightedCell is { } highlighted)
                PinToggleRequested?.Invoke(this, highlighted);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.R && Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (HighlightedCell is { } highlighted)
                RevealRequested?.Invoke(this, highlighted);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (HighlightedCell is { } highlighted)
                CopyCommandRequested?.Invoke(this, highlighted);
            e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            case Key.Down:
                MoveSearchHighlight(+1);
                e.Handled = true;
                break;
            case Key.Up:
                MoveSearchHighlight(-1);
                e.Handled = true;
                break;
            case Key.Enter:
                SubmitSearch();
                e.Handled = true;
                break;
            case Key.Escape:
                ExitSearch();
                e.Handled = true;
                break;
        }
    }

    private HiveCell? HighlightedCell =>
        _results.Count == 0 ? null : _results[Math.Clamp(_highlight, 0, _results.Count - 1)];

    /// <summary>Key handling for the plain-grid mode (routed here from the overlay window).</summary>
    public void HandleWindowKeyDown(KeyEventArgs e)
    {
        if (e.Handled)
            return;

        // If an IME still sneaks a keystroke through, WPF reports ImeProcessed and
        // hides the real key in ImeProcessedKey — unwrap it so hotkeys keep firing.
        var key = e.Key == Key.ImeProcessed ? e.ImeProcessedKey : e.Key;

        if (ConfirmVisible)
        {
            if (key == Key.Enter)
                CommitConfirm();
            else if (key == Key.Escape)
                HideConfirm();
            e.Handled = true;
            return;
        }

        if (_searching)
            return;

        if (key == Key.Escape)
        {
            CloseRequested?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
            return;
        }
        if (key == Key.Back)
        {
            BackRequested?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
            return;
        }
        if (key == Key.Space)
        {
            EnterSearch();
            e.Handled = true;
            return;
        }
        if (key == Key.P && Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (_activeLayerKind == GridLayerKind.Home && _hoveredCell != null)
                PinToggleRequested?.Invoke(this, _hoveredCell);
            e.Handled = true;
            return;
        }
        // Plain letters only: modified chords (Ctrl+P etc.) must not trigger a cell.
        if (key is >= Key.A and <= Key.Z && Keyboard.Modifiers == ModifierKeys.None)
        {
            char letter = (char)('A' + (key - Key.A));
            var cell = _cells.FirstOrDefault(c => c.Letter == letter);
            if (cell != null)
            {
                CellChosen?.Invoke(this, cell);
                e.Handled = true;
            }
        }
    }

    public bool ConfirmVisible => ConfirmOverlay.Visibility == Visibility.Visible;

    /// <summary>Brief "copied" pill over the result list; fades out on its own.</summary>
    public void ShowCopyToast()
    {
        CopyToastText.Text = Loc.Get("Grid_CopiedToast");
        CopyToast.BeginAnimation(UIElement.OpacityProperty, null);
        var animation = new DoubleAnimationUsingKeyFrames();
        animation.KeyFrames.Add(new SplineDoubleKeyFrame(1,
            KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(120)),
            new KeySpline(0.22, 1, 0.36, 1)));
        animation.KeyFrames.Add(new SplineDoubleKeyFrame(1,
            KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(900))));
        animation.KeyFrames.Add(new SplineDoubleKeyFrame(0,
            KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1300)),
            new KeySpline(0.22, 1, 0.36, 1)));
        CopyToast.BeginAnimation(UIElement.OpacityProperty, animation);
    }

    /// <summary>Shows the in-overlay confirm dialog; null action turns it into a notice.</summary>
    public void ShowConfirm(string message, string confirmText, Action? onConfirm)
    {
        ConfirmMessage.Text = message;
        ConfirmYesText.Text = confirmText;
        ConfirmYes.Visibility = onConfirm != null ? Visibility.Visible : Visibility.Collapsed;
        ConfirmNoText.Text = Loc.Get(onConfirm != null ? "Common_Cancel" : "Common_Ok");
        _confirmAction = onConfirm;
        ConfirmOverlay.Visibility = Visibility.Visible;
    }

    private void HideConfirm()
    {
        _confirmAction = null;
        ConfirmOverlay.Visibility = Visibility.Collapsed;
    }

    private void CommitConfirm()
    {
        var action = _confirmAction;
        HideConfirm();
        action?.Invoke();
    }

    private void OnConfirmYesClick(object sender, MouseButtonEventArgs e)
    {
        CommitConfirm();
        e.Handled = true;
    }

    private void OnConfirmNoClick(object sender, MouseButtonEventArgs e)
    {
        HideConfirm();
        e.Handled = true;
    }

    private void OnConfirmBackdropClick(object sender, MouseButtonEventArgs e)
    {
        HideConfirm();
        e.Handled = true;
    }

    private void OnConfirmDialogClick(object sender, MouseButtonEventArgs e)
    {
        // Clicks inside the dialog must not reach the cancel-on-backdrop handler.
        e.Handled = true;
    }

    public void MoveSearchHighlight(int delta)
    {
        if (!_searching || _results.Count == 0)
            return;
        SetHighlight(Math.Clamp(_highlight + delta, 0, _results.Count - 1));
    }

    public void SubmitSearch()
    {
        if (!_searching || _results.Count == 0)
            return;
        CellChosen?.Invoke(this, _results[Math.Clamp(_highlight, 0, _results.Count - 1)]);
    }

    private void ShowPreview(HiveCell cell)
    {
        if (_searching || OverlayHwnd == IntPtr.Zero)
        {
            HidePreview();
            return;
        }

        // Each motion kind declares its own hover content; plain running windows
        // (no motion) fall back to the live thumbnail.
        var hover = cell.Motion?.DescribeHover(cell)
            ?? (cell.IsRunning ? MotionHoverPreview.Thumbnail : MotionHoverPreview.None);

        switch (hover.Kind)
        {
            case MotionHoverKind.WindowThumbnail:
                ShowThumbnailPreview(cell);
                break;
            case MotionHoverKind.Info:
                ShowInfoPreview(cell, hover);
                break;
            default:
                HidePreview();
                break;
        }
    }

    private void ShowThumbnailPreview(HiveCell cell)
    {
        if (!cell.IsRunning)
        {
            HidePreview();
            return;
        }

        LaunchInfoPanel.Visibility = Visibility.Collapsed;
        HoverPreviewViewport.Visibility = Visibility.Visible;

        var (contentW, contentH) = GetWindowContentSize(cell.WindowHandle);
        if (!_dwmPreview.TryRegister(OverlayHwnd, cell.WindowHandle, out var sourceSize))
        {
            HidePreview();
            return;
        }

        if (sourceSize.cx > 40 && sourceSize.cy > 40)
            (contentW, contentH) = (sourceSize.cx, sourceSize.cy);

        if (contentW <= 0)
        {
            HidePreview();
            return;
        }

        double availW = Root.ActualWidth * 0.45;
        double availH = _previewMaxH;
        double scale = Math.Min(availW / contentW, availH / contentH);
        HoverPreviewViewport.Width = contentW * scale;
        HoverPreviewViewport.Height = contentH * scale;

        bool wasVisible = _previewVisible;
        _previewVisible = true;
        _previewMode = PreviewMode.Thumbnail;
        if (!wasVisible)
            SplineAnimate(HoverPreview, UIElement.OpacityProperty, 1, 160);

        // Attach the thumbnail once the frame's new size has been arranged.
        var handle = cell.WindowHandle;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
            new Action(() => AttachThumbnail(handle)));
    }

    private void ShowInfoPreview(HiveCell cell, MotionHoverPreview info)
    {
        _dwmPreview.Hide();
        HoverPreviewViewport.Visibility = Visibility.Collapsed;
        LaunchInfoPanel.Visibility = Visibility.Visible;
        LaunchInfoName.Text = info.Title;
        LaunchInfoCommand.Text = info.Detail;
        LaunchInfoHint.Text = Loc.Get(cell.Motion is { IsConfigured: false }
            ? "Cell_NotConfigured"
            : cell.Folder != null || cell.WindowView != null ? "Cell_ClickToOpen" : "Cell_ClickToLaunch");

        bool wasVisible = _previewVisible;
        _previewVisible = true;
        _previewMode = PreviewMode.LaunchInfo;
        if (!wasVisible)
            SplineAnimate(HoverPreview, UIElement.OpacityProperty, 1, 160);
    }

    private void AttachThumbnail(IntPtr sourceHwnd)
    {
        if (!_previewVisible || _previewMode != PreviewMode.Thumbnail || OverlayHwnd == IntPtr.Zero ||
            _dwmPreview.CurrentSource != sourceHwnd)
            return;

        var dpi = VisualTreeHelper.GetDpi(this);
        var topLeft = HoverPreviewViewport.PointToScreen(new Point(0, 0));

        // DWM thumbnail destination is in the overlay's CLIENT coordinates, not screen
        // coordinates — identical on the primary monitor at (0,0), off by the monitor
        // origin on any other screen.
        NativeMethods.GetWindowRect(OverlayHwnd, out var windowRect);
        var rect = new NativeMethods.RECT
        {
            Left = (int)Math.Round(topLeft.X - windowRect.Left),
            Top = (int)Math.Round(topLeft.Y - windowRect.Top),
            Right = (int)Math.Round(topLeft.X - windowRect.Left + HoverPreviewViewport.ActualWidth * dpi.DpiScaleX),
            Bottom = (int)Math.Round(topLeft.Y - windowRect.Top + HoverPreviewViewport.ActualHeight * dpi.DpiScaleY)
        };
        _dwmPreview.Show(rect);
    }

    private void HidePreview()
    {
        _dwmPreview.Hide();
        _previewMode = PreviewMode.None;
        if (!_previewVisible)
            return;
        _previewVisible = false;
        SplineAnimate(HoverPreview, UIElement.OpacityProperty, 0, 140);
    }

    public void ResetPreview() => HidePreview();

    /// <summary>Aspect source for fitting the preview: restore rect for minimized windows, client rect otherwise.</summary>
    private static (double w, double h) GetWindowContentSize(IntPtr hwnd)
    {
        if (NativeMethods.IsIconic(hwnd))
        {
            var placement = new NativeMethods.WINDOWPLACEMENT { length = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.WINDOWPLACEMENT>() };
            if (NativeMethods.GetWindowPlacement(hwnd, ref placement))
            {
                double w = placement.rcNormalPosition.Right - placement.rcNormalPosition.Left;
                double h = placement.rcNormalPosition.Bottom - placement.rcNormalPosition.Top;
                if (w > 40 && h > 40)
                    return (w, h);
            }
        }

        if (NativeMethods.GetClientRect(hwnd, out var client))
        {
            double w = client.Right - client.Left;
            double h = client.Bottom - client.Top;
            if (w > 40 && h > 40)
                return (w, h);
        }
        return (0, 0);
    }

    private void RebuildResults()
    {
        _resultsVersion = _cellsVersion;
        RebuildResultsCount++;
        _results = _cells
            .Where(MatchesQuery)
            .OrderBy(c => c.IsRunning ? 0 : 1)
            .ThenBy(c => c.Letter)
            .ToList();

        for (int i = 0; i < _itemVisuals.Count; i++)
        {
            var visual = _itemVisuals[i];
            if (i < _results.Count)
                UpdateResultItem(visual, _results[i]);
            else
            {
                visual.Cell = null;
                visual.Root.Visibility = Visibility.Collapsed;
            }
        }

        ResultHeaderText.Text = Loc.Format("Grid_AllWindowsCount", _results.Count);
        ResultEmpty.Visibility = _results.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ResultHeader.Visibility = _results.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        SetHighlight(0);
    }

    /// <summary>Builds the result list only when the cells changed since the last build.</summary>
    private void EnsureResultsBuilt()
    {
        if (_resultsVersion != _cellsVersion)
            RebuildResults();
    }

    /// <summary>Number of result rebuilds; test-only instrumentation for the deferred build.</summary>
    internal int RebuildResultsCount { get; private set; }

    private bool MatchesQuery(HiveCell cell)
    {
        if (string.IsNullOrWhiteSpace(_query))
            return true;
        string q = _query.Trim();
        return cell.Title.Contains(q, StringComparison.OrdinalIgnoreCase)
            || cell.AppName.Contains(q, StringComparison.OrdinalIgnoreCase)
            || (q.Length == 1 && char.ToUpperInvariant(q[0]) == cell.Letter);
    }

    private void CreateResultPool()
    {
        for (int index = 0; index < 26; index++)
        {
            var visual = BuildResultItem(index);
            _itemVisuals.Add(visual);
            ResultList.Children.Add(visual.Root);
        }
    }

    private SearchResultVisual BuildResultItem(int index)
    {
        var bar = new Border
        {
            Width = 3,
            Height = 24,
            CornerRadius = new CornerRadius(1.5),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(-9, 0, 0, 0),
            Visibility = Visibility.Hidden,
            Background = new LinearGradientBrush(
                (Color)ColorConverter.ConvertFromString("#FFE6A3"),
                (Color)ColorConverter.ConvertFromString("#F5B301"), 90)
        };

        var iconImage = new Image { Width = 24, Height = 24, Visibility = Visibility.Collapsed };
        RenderOptions.SetBitmapScalingMode(iconImage, BitmapScalingMode.HighQuality);
        var fallbackIcon = new TextBlock
        {
            Text = "?",
            FontSize = 14,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#99FFFFFF")),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var iconGrid = new Grid();
        iconGrid.Children.Add(iconImage);
        iconGrid.Children.Add(fallbackIcon);
        var iconBorder = new Border
        {
            Width = 40,
            Height = 32,
            CornerRadius = new CornerRadius(4),
            BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#26FFFFFF")),
            BorderThickness = new Thickness(1),
            ClipToBounds = true,
            Child = iconGrid,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        var title = new TextBlock
        {
            FontSize = 13,
            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#E6FFFFFF")),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var subtitle = new TextBlock
        {
            FontSize = 11,
            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#66FFFFFF")),
            Margin = new Thickness(0, 2, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var texts = new StackPanel { Margin = new Thickness(12, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(title);
        texts.Children.Add(subtitle);

        var statusDot = new Border
        {
            Width = 6,
            Height = 6,
            CornerRadius = new CornerRadius(3),
            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F5B301")),
            Margin = new Thickness(0, 0, 5, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed
        };
        var statusText = new TextBlock { FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        var status = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        status.Children.Add(statusDot);
        status.Children.Add(statusText);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(iconBorder, 0);
        Grid.SetColumn(texts, 1);
        Grid.SetColumn(status, 2);
        grid.Children.Add(bar);
        grid.Children.Add(iconBorder);
        grid.Children.Add(texts);
        grid.Children.Add(status);

        var root = new Border
        {
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(12, 8, 12, 8),
            Margin = new Thickness(2),
            Cursor = Cursors.Hand,
            Child = grid,
            Tag = index,
            Visibility = Visibility.Collapsed
        };
        root.MouseEnter += (_, _) => SetHighlight(index);
        root.MouseLeftButtonUp += (_, e) =>
        {
            if (_itemVisuals[index].Cell is { } cell)
                CellChosen?.Invoke(this, cell);
            e.Handled = true;
        };

        return new SearchResultVisual
        {
            Root = root,
            Bar = bar,
            IconImage = iconImage,
            FallbackIcon = fallbackIcon,
            Title = title,
            Subtitle = subtitle,
            StatusDot = statusDot,
            StatusText = statusText
        };
    }

    private void UpdateResultItem(SearchResultVisual visual, HiveCell cell)
    {
        visual.Cell = cell;
        visual.Root.Visibility = Visibility.Visible;
        UpdateResultIcon(visual, cell);
        visual.Title.Text = cell.Title;
        visual.Subtitle.Text = cell.AppName;

        visual.StatusDot.Visibility = cell.IsRunning ? Visibility.Visible : Visibility.Collapsed;
        visual.StatusText.Text = cell.SystemAction != null
            ? Loc.Get("Grid_StatusSystemAction")
            : Loc.Get(cell.IsRunning ? "Grid_StatusRunning" : "Grid_StatusNotRunning");
        visual.StatusText.Foreground = cell.SystemAction != null || cell.IsRunning
            ? ActiveStatusBrush
            : InactiveStatusBrush;
    }

    private void UpdateResultIcon(SearchResultVisual visual, HiveCell cell)
    {
        var icon = ResolveIcon(cell);
        if (!ReferenceEquals(visual.IconImage.Source, icon)) visual.IconImage.Source = icon;
        visual.IconImage.Opacity = cell.IsRunning ? 1 : 0.55;
        visual.IconImage.Visibility = icon != null ? Visibility.Visible : Visibility.Collapsed;
        visual.FallbackIcon.Text = string.IsNullOrEmpty(cell.AppName) ? "?" : cell.AppName[..1].ToUpperInvariant();
        visual.FallbackIcon.Visibility = icon == null ? Visibility.Visible : Visibility.Collapsed;
    }

    private static SolidColorBrush FrozenBrush(string value)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
        brush.Freeze();
        return brush;
    }

    private void SetHighlight(int index)
    {
        if (_results.Count == 0)
        {
            _highlight = 0;
            return;
        }
        index = Math.Clamp(index, 0, _results.Count - 1);
        _highlight = index;

        for (int i = 0; i < _itemVisuals.Count; i++)
        {
            bool active = i == index;
            _itemVisuals[i].Root.Background = active ? HighlightRowBrush : Brushes.Transparent;
            _itemVisuals[i].Bar.Visibility = active ? Visibility.Visible : Visibility.Hidden;
        }
        _itemVisuals[index].Root.BringIntoView();
    }

    private void OnRootSizeChanged(object sender, SizeChangedEventArgs e)
    {
        double w = Root.ActualWidth;
        double h = Root.ActualHeight;
        HiveBox.MaxWidth = w * 0.92;
        HiveBox.MaxHeight = h * 0.52;

        // Keep the hover preview inside the empty band above the hive, never over the cells.
        double gridScale = Math.Min(1, Math.Min(w * 0.92 / 1470, h * 0.52 / 460));
        double gridAreaHeight = 460 * gridScale + 28 + 48;
        double topSpace = (h - gridAreaHeight) / 2;
        _previewMaxH = Math.Clamp(topSpace - 48, 160, 320);
        HoverPreview.MaxWidth = w * 0.45;
    }

    private void OnBackdropClick(object sender, MouseButtonEventArgs e)
    {
        if (_searching)
            ExitSearch();
        else
            CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnSpaceBarClick(object sender, MouseButtonEventArgs e)
    {
        if (!_searching)
        {
            EnterSearch();
            e.Handled = true;
        }
    }

    private static void SplineAnimate(IAnimatable target, DependencyProperty property, double to, double durationMs, double delayMs = 0)
    {
        var animation = new DoubleAnimationUsingKeyFrames
        {
            BeginTime = TimeSpan.FromMilliseconds(delayMs)
        };
        animation.KeyFrames.Add(new SplineDoubleKeyFrame(
            to,
            KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(durationMs)),
            new KeySpline(0.22, 1, 0.36, 1)));
        target.BeginAnimation(property, animation);
    }
}
