using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using HiveMotion.Localization;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Image = System.Windows.Controls.Image;
using Orientation = System.Windows.Controls.Orientation;
using Path = System.IO.Path;
using Point = System.Windows.Point;

namespace HiveMotion;

/// <summary>
/// Manage center: pin overview (drag a tile to move/swap its letter), pin editor,
/// launch-history picker, and general settings. Edits write through to the stores
/// immediately; the overlay picks them up on its next open.
/// </summary>
public partial class ManageWindow : Window
{
    private const string DragFormat = "HiveMotion.PinLetter";

    private readonly PinStore _pinStore;
    private readonly HistoryStore _historyStore;
    private readonly SettingsStore _settingsStore;
    private readonly AutoStartManager _autoStartManager;
    private readonly WindowScanner _windowScanner;
    private readonly Action _applyHotkeys;
    private readonly System.Windows.Threading.DispatcherTimer _statusTimer;
    private readonly List<(Ellipse Dot, char Letter)> _tileDots = new();

    private IReadOnlyList<RunningWindow> _windows = Array.Empty<RunningWindow>();
    private PinnedApp? _selectedPin;
    private char _pickerLetter;
    private Action? _confirmAction;
    private bool _editorLoading;
    private bool _dragArmed;
    private char _dragLetter;
    private Point _dragStart;
    private bool _capturingHotkey;

    public ManageWindow(PinStore pinStore, HistoryStore historyStore, SettingsStore settingsStore,
        AutoStartManager autoStartManager, WindowScanner windowScanner, Action applyHotkeys)
    {
        InitializeComponent();
        _pinStore = pinStore;
        _historyStore = historyStore;
        _settingsStore = settingsStore;
        _autoStartManager = autoStartManager;
        _windowScanner = windowScanner;
        _applyHotkeys = applyHotkeys;

        Rescan();
        BuildLetterTiles();
        ShowEditor(null);
        SetNav(0);
        BuildPriorityList();
        RefreshHotkeyUi();
        InitAboutPage();

        AutoStartBox.IsChecked = _autoStartManager.IsAutoStartEnabled();
        VerboseLoggingBox.IsChecked = _settingsStore.Settings.VerboseLogging;
        ConfigPathText.Text = PinStore.StoreDirectoryPath;
        UpdateHistoryCount();

        _statusTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _statusTimer.Tick += (_, _) => Rescan();
        _statusTimer.Start();
        UpdateLanguageButtons();
        LocalizationManager.Instance.CultureChanged += OnCultureChanged;
        Closed += (_, _) =>
        {
            _statusTimer.Stop();
            LocalizationManager.Instance.CultureChanged -= OnCultureChanged;
        };
    }

    /// <summary>Re-applies every code-set string after the UI language switched.</summary>
    private void OnCultureChanged(object? sender, EventArgs e) => ApplyLocalizedStrings();

    private void ApplyLocalizedStrings()
    {
        BuildLetterTiles();
        UpdateEditorStatus();
        UpdateHistoryCount();
        RefreshHotkeyUi();
        InitAboutPage();
        if (PickerOverlay.Visibility == Visibility.Visible)
            RebuildPickerList();
        UpdateLanguageButtons();
    }

    // ---------- status ----------

    private void Rescan()
    {
        try
        {
            _windows = _windowScanner.Scan();
        }
        catch
        {
            _windows = Array.Empty<RunningWindow>();
        }
        UpdateTileStatus();
        UpdateEditorStatus();
    }

    private bool IsIdentityRunning(PinnedApp pin) => _windows.Any(pin.Matches);

    // ---------- navigation ----------

    private void SetNav(int page)
    {
        var active = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1FF5B301"));
        NavPins.Background = page == 0 ? active : Brushes.Transparent;
        NavPriority.Background = page == 1 ? active : Brushes.Transparent;
        NavHotkeys.Background = page == 2 ? active : Brushes.Transparent;
        NavGeneral.Background = page == 3 ? active : Brushes.Transparent;
        NavAbout.Background = page == 4 ? active : Brushes.Transparent;
        PagePins.Visibility = page == 0 ? Visibility.Visible : Visibility.Collapsed;
        PagePriority.Visibility = page == 1 ? Visibility.Visible : Visibility.Collapsed;
        PageHotkeys.Visibility = page == 2 ? Visibility.Visible : Visibility.Collapsed;
        PageGeneral.Visibility = page == 3 ? Visibility.Visible : Visibility.Collapsed;
        PageAbout.Visibility = page == 4 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnNavPinsClick(object sender, MouseButtonEventArgs e) => SetNav(0);
    private void OnNavPriorityClick(object sender, MouseButtonEventArgs e) => SetNav(1);
    private void OnNavHotkeysClick(object sender, MouseButtonEventArgs e) => SetNav(2);
    private void OnNavGeneralClick(object sender, MouseButtonEventArgs e) => SetNav(3);
    private void OnNavAboutClick(object sender, MouseButtonEventArgs e) => SetNav(4);

    // ---------- letter tiles ----------

    private void BuildLetterTiles()
    {
        LetterRows.Children.Clear();
        _tileDots.Clear();

        foreach (var row in KeyGrid.Rows)
        {
            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 3, 0, 3)
            };
            foreach (char letter in row)
                panel.Children.Add(BuildTile(letter));
            LetterRows.Children.Add(panel);
        }

        UpdateTileStatus();
    }

    private Border BuildTile(char letter)
    {
        var pin = _pinStore.FindByKey(letter);
        var tile = new Border
        {
            Width = 60,
            Height = 60,
            Margin = new Thickness(4),
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            Tag = letter,
            AllowDrop = true,
            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(pin != null ? "#1AFFFFFF" : "#0AFFFFFF")),
            BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(pin != null ? "#80F5B301" : "#26FFFFFF")),
            ToolTip = pin != null ? $"{letter}: {pin.DisplayName}" : Loc.Format("Pins_PinToLetter", letter)
        };

        var content = new Grid();
        if (pin != null)
        {
            var icon = IconHelper.ForExecutable(pin.ExecutablePath);
            var image = new Image
            {
                Width = 28,
                Height = 28,
                Source = icon,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            content.Children.Add(image);
            if (icon == null)
            {
                content.Children.Add(new TextBlock
                {
                    Text = pin.DisplayName.Length > 0 ? pin.DisplayName.Substring(0, 1) : "?",
                    FontSize = 18,
                    FontWeight = FontWeights.Bold,
                    Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#99FFFFFF")),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                });
            }

            content.Children.Add(new TextBlock
            {
                Text = letter.ToString(),
                FontSize = 10,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#CCFFD97A")),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(6, 4, 0, 0)
            });

            var dot = new Ellipse
            {
                Width = 8,
                Height = 8,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 6, 5)
            };
            content.Children.Add(dot);
            _tileDots.Add((dot, letter));

            tile.Cursor = Cursors.Hand;
            tile.PreviewMouseLeftButtonDown += OnTileDragStart;
            tile.PreviewMouseMove += OnTileDragMove;
            tile.MouseLeftButtonUp += OnPinnedTileClick;
        }
        else
        {
            content.Children.Add(new TextBlock
            {
                Text = letter.ToString(),
                FontSize = 16,
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#59FFFFFF")),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            });
            tile.Cursor = Cursors.Hand;
            tile.MouseLeftButtonUp += OnEmptyTileClick;
        }

        tile.Child = content;
        tile.Drop += OnTileDrop;
        tile.DragEnter += OnTileDragEnter;
        tile.DragLeave += OnTileDragLeave;
        return tile;
    }

    private void UpdateTileStatus()
    {
        foreach (var (dot, letter) in _tileDots)
        {
            var pin = _pinStore.FindByKey(letter);
            bool running = pin != null && IsIdentityRunning(pin);
            dot.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(running ? "#CC7CFC00" : "#59FFFFFF"));
        }
    }

    // ---------- drag & drop ----------

    private void OnTileDragStart(object sender, MouseButtonEventArgs e)
    {
        _dragArmed = true;
        _dragStart = e.GetPosition(this);
        _dragLetter = (char)((Border)sender).Tag;
    }

    private void OnTileDragMove(object sender, MouseEventArgs e)
    {
        if (!_dragArmed || e.LeftButton != MouseButtonState.Pressed)
            return;
        var pos = e.GetPosition(this);
        if (Math.Abs(pos.X - _dragStart.X) < 8 && Math.Abs(pos.Y - _dragStart.Y) < 8)
            return;
        _dragArmed = false;
        DragDrop.DoDragDrop((Border)sender,
            new DataObject(DragFormat, _dragLetter), DragDropEffects.Move);
    }

    private void OnTileDragEnter(object sender, DragEventArgs e)
    {
        var tile = (Border)sender;
        if (!e.Data.GetDataPresent(DragFormat))
            return;
        e.Effects = DragDropEffects.Move;
        tile.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFF5B301"));
        e.Handled = true;
    }

    private void OnTileDragLeave(object sender, DragEventArgs e)
    {
        var tile = (Border)sender;
        char letter = (char)tile.Tag;
        tile.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(
            _pinStore.FindByKey(letter) != null ? "#80F5B301" : "#26FFFFFF"));
    }

    private void OnTileDrop(object sender, DragEventArgs e)
    {
        OnTileDragLeave(sender, e);
        if (!e.Data.GetDataPresent(DragFormat))
            return;

        char target = (char)((Border)sender).Tag;
        char source = (char)e.Data.GetData(DragFormat);
        if (source == target)
            return;

        var sourcePin = _pinStore.FindByKey(source);
        if (sourcePin == null)
            return;
        var targetPin = _pinStore.FindByKey(target);

        // Move to an empty letter, or swap with the occupying pin.
        _pinStore.Remove(source);
        _pinStore.Remove(target);
        sourcePin.Key = target;
        if (targetPin != null)
        {
            targetPin.Key = source;
            _pinStore.Set(targetPin);
        }
        _pinStore.Set(sourcePin);

        BuildLetterTiles();
        ShowEditor(sourcePin);
        e.Handled = true;
    }

    private void OnPinnedTileClick(object sender, MouseButtonEventArgs e)
    {
        _dragArmed = false;
        char letter = (char)((Border)sender).Tag;
        ShowEditor(_pinStore.FindByKey(letter));
        e.Handled = true;
    }

    private void OnEmptyTileClick(object sender, MouseButtonEventArgs e)
    {
        OpenPicker((char)((Border)sender).Tag);
        e.Handled = true;
    }

    // ---------- editor ----------

    private void ShowEditor(PinnedApp? pin)
    {
        _selectedPin = pin;
        _editorLoading = true;

        if (pin == null)
        {
            EditorPanel.Visibility = Visibility.Collapsed;
            EditorEmpty.Visibility = Visibility.Visible;
        }
        else
        {
            EditorEmpty.Visibility = Visibility.Collapsed;
            EditorPanel.Visibility = Visibility.Visible;
            EditorLetterBadge.Text = pin.Key.ToString();
            EditorIcon.Source = IconHelper.ForExecutable(pin.ExecutablePath);
            EditorName.Text = pin.DisplayName;
            EditorPath.Text = pin.ExecutablePath;
            EditorArgs.Text = pin.Arguments;
            EditorCwd.Text = pin.WorkingDirectory;
            UpdateEditorStatus();
            UpdatePreview();
            ValidatePath();
        }

        _editorLoading = false;
    }

    private void UpdateEditorStatus()
    {
        if (_selectedPin == null)
            return;
        bool running = IsIdentityRunning(_selectedPin);
        EditorStatus.Text = Loc.Get(running ? "Pins_StatusRunning" : "Pins_StatusNotRunning");
        EditorStatus.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(
            running ? "#CC7CFC00" : "#66FFFFFF"));
    }

    private void UpdatePreview()
    {
        string path = EditorPath.Text.Trim();
        string args = EditorArgs.Text.Trim();
        EditorPreview.Text = args.Length == 0 ? path : $"{path} {args}";
    }

    private void ValidatePath()
    {
        string path = EditorPath.Text.Trim();
        bool missing = path.Length > 0 && !File.Exists(path);
        PathWarning.Visibility = missing ? Visibility.Visible : Visibility.Collapsed;
        EditorPath.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(
            missing ? "#E5484D" : "#33FFFFFF"));
    }

    /// <summary>Edits write through immediately; every keystroke already updates the preview.</summary>
    private void CommitEditor()
    {
        if (_editorLoading || _selectedPin == null)
            return;

        _selectedPin.DisplayName = EditorName.Text.Trim();
        _selectedPin.ExecutablePath = EditorPath.Text.Trim();
        _selectedPin.Arguments = EditorArgs.Text.Trim();
        _selectedPin.WorkingDirectory = EditorCwd.Text.Trim();
        // An empty display name falls back to the executable's file name.
        if (_selectedPin.DisplayName.Length == 0 && _selectedPin.ExecutablePath.Length > 0)
            _selectedPin.DisplayName = Path.GetFileNameWithoutExtension(_selectedPin.ExecutablePath);
        _pinStore.Set(_selectedPin);
        BuildLetterTiles();
        UpdateEditorStatus();
    }

    private void OnEditorFieldLostFocus(object sender, RoutedEventArgs e) => CommitEditor();

    private void OnEditorPathChanged(object sender, TextChangedEventArgs e)
    {
        if (_editorLoading)
            return;
        UpdatePreview();
        ValidatePath();
    }

    private void OnEditorArgsChanged(object sender, TextChangedEventArgs e)
    {
        if (_editorLoading)
            return;
        UpdatePreview();
    }

    private void OnBrowsePathClick(object sender, MouseButtonEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = Loc.Get("Dialog_ExeFilter"),
            Title = Loc.Get("Dialog_SelectProgram")
        };
        if (dialog.ShowDialog(this) == true)
        {
            EditorPath.Text = dialog.FileName;
            CommitEditor();
            EditorIcon.Source = IconHelper.ForExecutable(dialog.FileName);
        }
        e.Handled = true;
    }

    private void OnBrowseCwdClick(object sender, MouseButtonEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = Loc.Get("Dialog_SelectWorkingDir")
        };
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            EditorCwd.Text = dialog.SelectedPath;
            CommitEditor();
        }
        e.Handled = true;
    }

    private void OnLaunchClick(object sender, MouseButtonEventArgs e)
    {
        CommitEditor();
        if (_selectedPin != null && _selectedPin.ExecutablePath.Length > 0)
            WindowManager.Launch(_selectedPin.ExecutablePath, _selectedPin.Arguments, _selectedPin.WorkingDirectory);
        e.Handled = true;
    }

    private void OnDeletePinClick(object sender, MouseButtonEventArgs e)
    {
        if (_selectedPin == null)
            return;
        var pin = _selectedPin;
        ShowConfirm(Loc.Format("Pins_DeleteConfirm", pin.Key, pin.DisplayName), () =>
        {
            _pinStore.Remove(pin.Key);
            BuildLetterTiles();
            ShowEditor(null);
        });
        e.Handled = true;
    }

    private void OnClearPinsClick(object sender, MouseButtonEventArgs e)
    {
        ShowConfirm(Loc.Get("Pins_ClearAllConfirm"), () =>
        {
            foreach (var pin in _pinStore.Pins.ToList())
                _pinStore.Remove(pin.Key);
            BuildLetterTiles();
            ShowEditor(null);
        });
        e.Handled = true;
    }

    // ---------- history picker ----------

    private void OpenPicker(char letter)
    {
        _pickerLetter = letter;
        PickerTitle.Text = Loc.Format("Pins_PickerTitle", letter);
        PickerSearch.Text = string.Empty;
        RebuildPickerList();
        PickerOverlay.Visibility = Visibility.Visible;
        PickerSearch.Focus();
    }

    private void ClosePicker()
    {
        PickerOverlay.Visibility = Visibility.Collapsed;
    }

    private void RebuildPickerList()
    {
        PickerList.Children.Clear();

        string query = PickerSearch.Text.Trim();
        var runningKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var window in _windows)
        {
            if (window.ExecutablePath != null)
                runningKeys.Add(HistoryEntry.Key(window.ExecutablePath, window.CommandLineArguments));
        }

        var entries = _historyStore.SortedForPicker()
            .Where(entry => query.Length == 0
                || entry.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)
                || entry.ExecutablePath.Contains(query, StringComparison.OrdinalIgnoreCase)
                || entry.Arguments.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (entries.Count == 0)
        {
            PickerList.Children.Add(new TextBlock
            {
                Text = Loc.Get("Picker_Empty"),
                FontSize = 12,
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#59FFFFFF")),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 24, 0, 24)
            });
            return;
        }

        foreach (var entry in entries)
            PickerList.Children.Add(BuildPickerItem(entry, runningKeys.Contains(entry.IdentityKey)));
    }

    private Border BuildPickerItem(HistoryEntry entry, bool isRunning)
    {
        bool missing = !File.Exists(entry.ExecutablePath);

        var image = new Image
        {
            Width = 28,
            Height = 28,
            Source = IconHelper.ForExecutable(entry.ExecutablePath),
            VerticalAlignment = VerticalAlignment.Center
        };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);

        var texts = new StackPanel { Margin = new Thickness(10, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(new TextBlock
        {
            Text = entry.DisplayName,
            FontSize = 13,
            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#E6FFFFFF")),
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        texts.Children.Add(new TextBlock
        {
            Text = entry.CommandLine + (missing ? Loc.Get("Picker_FileMissing") : ""),
            FontSize = 10,
            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#66FFFFFF")),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 2, 0, 0)
        });

        var meta = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        if (isRunning)
        {
            meta.Children.Add(new TextBlock
            {
                Text = Loc.Get("Picker_RunningPrefix"),
                FontSize = 10,
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#CC7CFC00")),
                VerticalAlignment = VerticalAlignment.Center
            });
        }
        meta.Children.Add(new TextBlock
        {
            Text = Loc.Plural("Picker_LaunchMeta", entry.LaunchCount, entry.LaunchCount, RelativeTime(entry.LastSeen)),
            FontSize = 10,
            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#66FFFFFF")),
            VerticalAlignment = VerticalAlignment.Center
        });

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(image, 0);
        Grid.SetColumn(texts, 1);
        Grid.SetColumn(meta, 2);
        grid.Children.Add(image);
        grid.Children.Add(texts);
        grid.Children.Add(meta);

        var row = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 7, 10, 7),
            Margin = new Thickness(2),
            Cursor = Cursors.Hand,
            Opacity = missing ? 0.5 : 1,
            Child = grid
        };
        row.MouseEnter += (_, _) =>
            row.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#14F5B301"));
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        row.MouseLeftButtonUp += (_, e) =>
        {
            PickEntry(new PinnedApp
            {
                Key = _pickerLetter,
                ProcessName = entry.ProcessName,
                ExecutablePath = entry.ExecutablePath,
                Arguments = entry.Arguments,
                WorkingDirectory = entry.WorkingDirectory,
                DisplayName = entry.DisplayName
            });
            e.Handled = true;
        };
        return row;
    }

    private void PickEntry(PinnedApp pin)
    {
        _pinStore.Set(pin);
        ClosePicker();
        BuildLetterTiles();
        ShowEditor(pin);
    }

    private void OnPickerSearchChanged(object sender, TextChangedEventArgs e) => RebuildPickerList();

    private void OnPickerManualClick(object sender, MouseButtonEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = Loc.Get("Dialog_ExeFilter"),
            Title = Loc.Get("Dialog_SelectPinProgram")
        };
        if (dialog.ShowDialog(this) == true)
        {
            PickEntry(new PinnedApp
            {
                Key = _pickerLetter,
                ExecutablePath = dialog.FileName,
                DisplayName = Path.GetFileNameWithoutExtension(dialog.FileName)
            });
        }
        e.Handled = true;
    }

    private void OnPickerCancelClick(object sender, MouseButtonEventArgs e)
    {
        ClosePicker();
        e.Handled = true;
    }

    private void OnPickerBackdropClick(object sender, MouseButtonEventArgs e)
    {
        ClosePicker();
        e.Handled = true;
    }

    private void OnPickerDialogClick(object sender, MouseButtonEventArgs e) => e.Handled = true;

    // ---------- priority page ----------

    private List<string> PriorityNames => _settingsStore.Settings.PriorityProcessNames;

    private void BuildPriorityList()
    {
        PriorityList.Children.Clear();
        for (int i = 0; i < PriorityNames.Count; i++)
            PriorityList.Children.Add(BuildPriorityRow(i));
    }

    private Border BuildPriorityRow(int index)
    {
        string name = PriorityNames[index];

        var nameText = new TextBlock
        {
            Text = name,
            FontSize = 13,
            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#E6FFFFFF")),
            VerticalAlignment = VerticalAlignment.Center
        };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(BuildSmallButton("↑", index > 0, (_, _) => MovePriority(index, -1)));
        buttons.Children.Add(BuildSmallButton("↓", index < PriorityNames.Count - 1, (_, _) => MovePriority(index, +1)));
        buttons.Children.Add(BuildSmallButton("✕", true, (_, _) =>
        {
            PriorityNames.RemoveAt(index);
            _settingsStore.Save();
            BuildPriorityList();
        }));

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(nameText, 0);
        Grid.SetColumn(buttons, 1);
        grid.Children.Add(nameText);
        grid.Children.Add(buttons);

        return new Border
        {
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0DFFFFFF")),
            BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1FFFFFFF")),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 6, 12, 6),
            Margin = new Thickness(2),
            Child = grid
        };
    }

    private Border BuildSmallButton(string glyph, bool enabled, MouseButtonEventHandler onClick)
    {
        var button = new Border
        {
            Width = 26,
            Height = 22,
            Margin = new Thickness(3, 0, 0, 0),
            CornerRadius = new CornerRadius(5),
            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(enabled ? "#14FFFFFF" : "#08FFFFFF")),
            Opacity = enabled ? 1 : 0.4,
            Cursor = enabled ? Cursors.Hand : Cursors.Arrow,
            Child = new TextBlock
            {
                Text = glyph,
                FontSize = 11,
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#BFFFFFFF")),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        if (enabled)
            button.MouseLeftButtonUp += onClick;
        return button;
    }

    private void MovePriority(int index, int delta)
    {
        int target = index + delta;
        if (target < 0 || target >= PriorityNames.Count)
            return;
        (PriorityNames[index], PriorityNames[target]) = (PriorityNames[target], PriorityNames[index]);
        _settingsStore.Save();
        BuildPriorityList();
    }

    private void AddPriorityName()
    {
        string name = PriorityInput.Text.Trim();
        if (name.Length == 0)
            return;
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            name = name.Substring(0, name.Length - 4);
        if (PriorityNames.Any(n => n.Equals(name, StringComparison.OrdinalIgnoreCase)))
            return;
        PriorityNames.Add(name);
        _settingsStore.Save();
        PriorityInput.Text = string.Empty;
        BuildPriorityList();
    }

    private void OnAddPriorityClick(object sender, MouseButtonEventArgs e)
    {
        AddPriorityName();
        e.Handled = true;
    }

    private void OnPriorityInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            AddPriorityName();
            e.Handled = true;
        }
    }

    // ---------- hotkeys page ----------

    private void RefreshHotkeyUi()
    {
        var settings = _settingsStore.Settings;
        HotkeyCurrentText.Text = string.Join(", ", settings.Hotkeys.Select(r => r.Name));
        PassthroughBox.IsChecked = settings.SecondPressPassthrough;
        RecordHotkeyText.Text = Loc.Get(_capturingHotkey ? "Hotkeys_Recording" : "Hotkeys_Record");
        UpdateCheatsheet();
    }

    private void OnPassthroughChanged(object sender, RoutedEventArgs e)
    {
        _settingsStore.Settings.SecondPressPassthrough = PassthroughBox.IsChecked == true;
        _settingsStore.Save();
        _applyHotkeys();
    }

    private void OnRecordHotkeyClick(object sender, MouseButtonEventArgs e)
    {
        _capturingHotkey = true;
        HotkeyCaptureHint.Visibility = Visibility.Visible;
        HotkeyWarning.Visibility = Visibility.Collapsed;
        RecordHotkeyText.Text = Loc.Get("Hotkeys_Recording");
        e.Handled = true;
    }

    private void OnResetHotkeyClick(object sender, MouseButtonEventArgs e)
    {
        _capturingHotkey = false;
        HotkeyCaptureHint.Visibility = Visibility.Collapsed;
        RecordHotkeyText.Text = Loc.Get("Hotkeys_Record");
        _settingsStore.Settings.Hotkeys.Clear();
        _settingsStore.Settings.Hotkeys.Add(HotkeyRule.WinTab);
        _settingsStore.Save();
        _applyHotkeys();
        RefreshHotkeyUi();
        e.Handled = true;
    }

    private void CaptureHotkey(KeyEventArgs e)
    {
        e.Handled = true;
        if (e.Key == Key.Escape)
        {
            _capturingHotkey = false;
            HotkeyCaptureHint.Visibility = Visibility.Collapsed;
            RecordHotkeyText.Text = Loc.Get("Hotkeys_Record");
            return;
        }

        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LWin or Key.RWin or Key.LeftCtrl or Key.RightCtrl
            or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift)
            return; // wait for the non-modifier key of the chord

        var mods = Keyboard.Modifiers;
        if (mods == ModifierKeys.None)
        {
            HotkeyWarning.Text = Loc.Get("Hotkeys_NeedModifier");
            HotkeyWarning.Visibility = Visibility.Visible;
            return;
        }

        int vk = KeyInterop.VirtualKeyFromKey(key);
        bool win = (mods & ModifierKeys.Windows) != 0;
        bool ctrl = (mods & ModifierKeys.Control) != 0;
        bool alt = (mods & ModifierKeys.Alt) != 0;
        bool shift = (mods & ModifierKeys.Shift) != 0;

        // Win+Tab keeps its native-UI escape hatches; anything else is a plain combo.
        var rule = win && !ctrl && !alt && !shift && vk == NativeMethods.VK_TAB
            ? HotkeyRule.WinTab
            : new HotkeyRule
            {
                Win = win,
                Ctrl = ctrl,
                Alt = alt,
                Shift = shift,
                Vk = vk,
                Name = FormatComboName(win, ctrl, alt, shift, key)
            };

        _settingsStore.Settings.Hotkeys.Clear();
        _settingsStore.Settings.Hotkeys.Add(rule);
        _settingsStore.Save();
        _applyHotkeys();

        _capturingHotkey = false;
        HotkeyCaptureHint.Visibility = Visibility.Collapsed;
        RecordHotkeyText.Text = Loc.Get("Hotkeys_Record");

        if (win && !ctrl && !alt && !shift && vk is >= 0x41 and <= 0x5A)
        {
            // Win+Letter combos are mostly owned by the shell (Win+E, Win+R, Win+I…).
            HotkeyWarning.Text = Loc.Format("Hotkeys_Conflict", rule.Name);
            HotkeyWarning.Visibility = Visibility.Visible;
        }
        else
        {
            HotkeyWarning.Visibility = Visibility.Collapsed;
        }

        RefreshHotkeyUi();
    }

    private static string FormatComboName(bool win, bool ctrl, bool alt, bool shift, Key key)
    {
        var parts = new List<string>();
        if (win)
            parts.Add("Win");
        if (ctrl)
            parts.Add("Ctrl");
        if (alt)
            parts.Add("Alt");
        if (shift)
            parts.Add("Shift");
        parts.Add(key.ToString());
        return string.Join("+", parts);
    }

    // ---------- general page ----------

    private void OnAutoStartChecked(object sender, RoutedEventArgs e) => _autoStartManager.EnableAutoStart();
    private void OnAutoStartUnchecked(object sender, RoutedEventArgs e) => _autoStartManager.DisableAutoStart();

    private void OnVerboseLoggingChecked(object sender, RoutedEventArgs e)
    {
        _settingsStore.Settings.VerboseLogging = true;
        Logger.IsVerboseEnabled = true;
        _settingsStore.Save();
    }

    private void OnVerboseLoggingUnchecked(object sender, RoutedEventArgs e)
    {
        _settingsStore.Settings.VerboseLogging = false;
        Logger.IsVerboseEnabled = false;
        _settingsStore.Save();
    }

    private void OnLanguageSystemClick(object sender, MouseButtonEventArgs e) => SetLanguage(LocalizationManager.SystemSetting);
    private void OnLanguageZhClick(object sender, MouseButtonEventArgs e) => SetLanguage(LocalizationManager.ChineseCulture);
    private void OnLanguageEnClick(object sender, MouseButtonEventArgs e) => SetLanguage(LocalizationManager.EnglishCulture);

    private void SetLanguage(string language)
    {
        if (_settingsStore.Settings.Language == language)
            return;
        _settingsStore.Settings.Language = language;
        _settingsStore.Save();
        LocalizationManager.Instance.ApplyLanguageSetting(language);
        UpdateLanguageButtons();
    }

    private void UpdateLanguageButtons()
    {
        string current = _settingsStore.Settings.Language;
        HighlightLanguageButton(LangSystemButton, current == LocalizationManager.SystemSetting);
        HighlightLanguageButton(LangZhButton, current == LocalizationManager.ChineseCulture);
        HighlightLanguageButton(LangEnButton, current == LocalizationManager.EnglishCulture);
    }

    private static void HighlightLanguageButton(Border button, bool active)
    {
        button.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(active ? "#26F5B301" : "#14FFFFFF"));
        button.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(active ? "#80F5B301" : "#33FFFFFF"));
    }

    private void UpdateHistoryCount()
    {
        HistoryCountText.Text = Loc.Plural("General_HistoryCount", _historyStore.Entries.Count, _historyStore.Entries.Count);
    }

    private void OnOpenConfigDirClick(object sender, MouseButtonEventArgs e)
    {
        Directory.CreateDirectory(PinStore.StoreDirectoryPath);
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"\"{PinStore.StoreDirectoryPath}\"",
            UseShellExecute = true
        });
        e.Handled = true;
    }

    private void OnOpenPinsFileClick(object sender, MouseButtonEventArgs e)
    {
        if (File.Exists(PinStore.PinsFilePath))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = PinStore.PinsFilePath,
                UseShellExecute = true
            });
        }
        e.Handled = true;
    }

    private void OnClearHistoryClick(object sender, MouseButtonEventArgs e)
    {
        ShowConfirm(Loc.Get("General_ClearHistoryConfirm"), () =>
        {
            _historyStore.Clear();
            UpdateHistoryCount();
        });
        e.Handled = true;
    }

    // ---------- backup (export / import) ----------

    private sealed class ConfigBundle
    {
        public int Version { get; set; } = 1;
        public List<PinnedApp>? Pins { get; set; }
        public AppSettings? Settings { get; set; }
        public List<HistoryEntry>? History { get; set; }
    }

    private void OnExportClick(object sender, MouseButtonEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = Loc.Get("Dialog_ConfigFilter"),
            FileName = "hivemotion-config.json",
            Title = Loc.Get("Dialog_ExportConfig")
        };
        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            var bundle = new ConfigBundle
            {
                Pins = _pinStore.Pins.ToList(),
                Settings = _settingsStore.Settings,
                History = _historyStore.Entries.ToList()
            };
            File.WriteAllText(dialog.FileName,
                System.Text.Json.JsonSerializer.Serialize(bundle,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            ShowConfirm(Loc.Format("Backup_ExportFailed", ex.Message), () => { });
        }
        e.Handled = true;
    }

    private void OnImportClick(object sender, MouseButtonEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = Loc.Get("Dialog_ConfigFilter"),
            Title = Loc.Get("Dialog_ImportConfig")
        };
        if (dialog.ShowDialog(this) != true)
            return;

        ConfigBundle? bundle;
        try
        {
            bundle = System.Text.Json.JsonSerializer.Deserialize<ConfigBundle>(
                File.ReadAllText(dialog.FileName));
        }
        catch (Exception ex)
        {
            ShowConfirm(Loc.Format("Backup_ImportParseFailed", ex.Message), () => { });
            return;
        }
        if (bundle?.Pins == null && bundle?.Settings == null && bundle?.History == null)
        {
            ShowConfirm(Loc.Get("Backup_ImportEmpty"), () => { });
            return;
        }

        ShowConfirm(Loc.Get("Backup_ImportConfirm"), () => ApplyImport(bundle));
        e.Handled = true;
    }

    private void ApplyImport(ConfigBundle bundle)
    {
        if (bundle.Pins != null)
        {
            _pinStore.ReplaceAll(bundle.Pins.Where(p =>
                p.Key is >= 'A' and <= 'Z' && !string.IsNullOrEmpty(p.ExecutablePath)));
        }
        if (bundle.History != null)
        {
            _historyStore.ReplaceAll(bundle.History.Where(h => !string.IsNullOrEmpty(h.ExecutablePath)));
        }
        if (bundle.Settings != null)
        {
            var settings = _settingsStore.Settings;
            settings.PriorityProcessNames.Clear();
            settings.PriorityProcessNames.AddRange(bundle.Settings.PriorityProcessNames);
            settings.Hotkeys.Clear();
            settings.Hotkeys.AddRange(bundle.Settings.Hotkeys);
            settings.SecondPressPassthrough = bundle.Settings.SecondPressPassthrough;
            settings.Language = bundle.Settings.Language ?? LocalizationManager.SystemSetting;
            _settingsStore.Save();
            _applyHotkeys();
            LocalizationManager.Instance.ApplyLanguageSetting(settings.Language);
        }

        BuildLetterTiles();
        ShowEditor(null);
        BuildPriorityList();
        RefreshHotkeyUi();
        UpdateHistoryCount();
        UpdateCheatsheet();
    }

    // ---------- about page ----------

    private void InitAboutPage()
    {
        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = Loc.Format("About_Version", version?.ToString() ?? "?", PinStore.StoreDirectoryPath);
        UpdateCheatsheet();
    }

    private void UpdateCheatsheet()
    {
        CheatsheetList.Children.Clear();
        AddCheatsheet(string.Join(" / ", _settingsStore.Settings.Hotkeys.Select(r => r.Name)), Loc.Get("Cheat_Summon"));
        AddCheatsheet("A – Z", Loc.Get("Cheat_Jump"));
        AddCheatsheet(Loc.Get("Cheat_Space"), Loc.Get("Cheat_Search"));
        AddCheatsheet("Ctrl + P", Loc.Get("Cheat_Pin"));
        AddCheatsheet("Ctrl + R", Loc.Get("Cheat_Reveal"));
        AddCheatsheet("Ctrl + S", Loc.Get("Cheat_CopyCmd"));
        AddCheatsheet("Esc", Loc.Get("Cheat_Close"));
    }

    private void AddCheatsheet(string keys, string description)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(2, 3, 2, 3)
        };
        row.Children.Add(new Border
        {
            Padding = new Thickness(10, 2, 10, 2),
            CornerRadius = new CornerRadius(5),
            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#14F5B301")),
            BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#59F5B301")),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = keys,
                FontSize = 11,
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#CCFFD97A"))
            }
        });
        row.Children.Add(new TextBlock
        {
            Text = description,
            FontSize = 12,
            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#99FFFFFF")),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0)
        });
        CheatsheetList.Children.Add(row);
    }

    // ---------- confirm dialog ----------

    private void ShowConfirm(string message, Action onConfirm)
    {
        ConfirmMessage.Text = message;
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

    private void OnConfirmDialogClick(object sender, MouseButtonEventArgs e) => e.Handled = true;

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (_capturingHotkey)
        {
            CaptureHotkey(e);
            return;
        }
        if (e.Key != Key.Escape)
            return;
        if (ConfirmOverlay.Visibility == Visibility.Visible)
        {
            HideConfirm();
            e.Handled = true;
        }
        else if (PickerOverlay.Visibility == Visibility.Visible)
        {
            ClosePicker();
            e.Handled = true;
        }
    }

    private static string RelativeTime(DateTime time)
    {
        var span = DateTime.Now - time;
        if (span.TotalMinutes < 1)
            return Loc.Get("Time_JustNow");
        if (span.TotalHours < 1)
            return Loc.Format("Time_MinutesAgo", (int)span.TotalMinutes);
        if (time.Date == DateTime.Today)
            return Loc.Format("Time_Today", time.ToString("HH:mm"));
        if (time.Date == DateTime.Today.AddDays(-1))
            return Loc.Get("Time_Yesterday");
        if (span.TotalDays < 30)
            return Loc.Plural("Time_DaysAgo", (int)span.TotalDays, (int)span.TotalDays);
        return time.ToString(Loc.Get("Time_DateFormat"));
    }
}
