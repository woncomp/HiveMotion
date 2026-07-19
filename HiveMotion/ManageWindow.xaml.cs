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
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Image = System.Windows.Controls.Image;
using Orientation = System.Windows.Controls.Orientation;
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
    private readonly AutoStartManager _autoStartManager;
    private readonly WindowScanner _windowScanner;
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

    public ManageWindow(PinStore pinStore, HistoryStore historyStore,
        AutoStartManager autoStartManager, WindowScanner windowScanner)
    {
        InitializeComponent();
        _pinStore = pinStore;
        _historyStore = historyStore;
        _autoStartManager = autoStartManager;
        _windowScanner = windowScanner;

        Rescan();
        BuildLetterTiles();
        ShowEditor(null);
        SetNav(pins: true);

        AutoStartBox.IsChecked = _autoStartManager.IsAutoStartEnabled();
        ConfigPathText.Text = PinStore.StoreDirectoryPath;
        UpdateHistoryCount();

        _statusTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _statusTimer.Tick += (_, _) => Rescan();
        _statusTimer.Start();
        Closed += (_, _) => _statusTimer.Stop();
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

    private void SetNav(bool pins)
    {
        NavPins.Background = pins ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1FF5B301")) : Brushes.Transparent;
        NavGeneral.Background = pins ? Brushes.Transparent : new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1FF5B301"));
        PagePins.Visibility = pins ? Visibility.Visible : Visibility.Collapsed;
        PageGeneral.Visibility = pins ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnNavPinsClick(object sender, MouseButtonEventArgs e) => SetNav(pins: true);
    private void OnNavGeneralClick(object sender, MouseButtonEventArgs e) => SetNav(pins: false);

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
            ToolTip = pin != null ? $"{letter}: {pin.DisplayName}" : $"固定到 {letter}"
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
        EditorStatus.Text = running ? "● 运行中(有窗口匹配该命令行)" : "○ 未运行";
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
            Filter = "可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*",
            Title = "选择程序"
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
            Description = "选择工作目录"
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
        ShowConfirm($"移除固定在 {pin.Key} 键的「{pin.DisplayName}」?", () =>
        {
            _pinStore.Remove(pin.Key);
            BuildLetterTiles();
            ShowEditor(null);
        });
        e.Handled = true;
    }

    private void OnClearPinsClick(object sender, MouseButtonEventArgs e)
    {
        ShowConfirm("清空全部固定?此操作不可撤销。", () =>
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
        PickerTitle.Text = $"固定到 {letter} 键";
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
                Text = "没 有 匹 配 的 历 史 记 录",
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
            Text = entry.CommandLine + (missing ? "  (文件缺失)" : ""),
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
                Text = "运行中 · ",
                FontSize = 10,
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#CC7CFC00")),
                VerticalAlignment = VerticalAlignment.Center
            });
        }
        meta.Children.Add(new TextBlock
        {
            Text = $"启动 {entry.LaunchCount} 次 · {RelativeTime(entry.LastSeen)}",
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
            Filter = "可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*",
            Title = "选择要固定的程序"
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

    // ---------- general page ----------

    private void OnAutoStartChecked(object sender, RoutedEventArgs e) => _autoStartManager.EnableAutoStart();
    private void OnAutoStartUnchecked(object sender, RoutedEventArgs e) => _autoStartManager.DisableAutoStart();

    private void UpdateHistoryCount()
    {
        HistoryCountText.Text = $"启动历史:已记录 {_historyStore.Entries.Count} 条";
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
        ShowConfirm("清空全部启动历史?", () =>
        {
            _historyStore.Clear();
            UpdateHistoryCount();
        });
        e.Handled = true;
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
            return "刚刚";
        if (span.TotalHours < 1)
            return $"{(int)span.TotalMinutes} 分钟前";
        if (time.Date == DateTime.Today)
            return $"今天 {time:HH:mm}";
        if (time.Date == DateTime.Today.AddDays(-1))
            return "昨天";
        if (span.TotalDays < 30)
            return $"{(int)span.TotalDays} 天前";
        return time.ToString("yyyy-MM-dd");
    }
}
