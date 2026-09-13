using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace HiveMotion.ManageCenter;

/// <summary>
/// The shared header of the motion editor, identical for every motion kind: icon
/// (click to pick a custom one, badge to clear it), the bound letter, an editable
/// custom name, a kind label + status line, and the delete button. It mutates only
/// the shared <see cref="Motion"/> properties (DisplayName / IconPath) and reports
/// edits through <see cref="MotionChanged"/>; the host window persists and refreshes.
/// </summary>
public partial class MotionEditorHeader : UserControl
{
    private static readonly Brush StatusBrush = FrozenBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF));
    private static readonly Brush WarningBrush = FrozenBrush(Color.FromArgb(0x99, 0xFF, 0xD9, 0x7A));

    private Motion? _motion;
    private bool _loading;

    /// <summary>DisplayName or IconPath was edited; the host persists and rebuilds tiles.</summary>
    public event EventHandler? MotionChanged;

    /// <summary>The delete button was clicked; the host confirms and removes the motion.</summary>
    public event EventHandler? DeleteRequested;

    public MotionEditorHeader()
    {
        InitializeComponent();
    }

    private static Brush FrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    /// <summary>Rebinds every shared element to the given motion; null hides the header.</summary>
    public void Bind(Motion? motion)
    {
        _motion = motion;
        _loading = true;
        Visibility = motion == null ? Visibility.Collapsed : Visibility.Visible;
        if (motion != null)
        {
            LetterBadge.Text = motion.Key.ToString();
            NameBox.Text = motion.EffectiveName;
        }
        Refresh();
        _loading = false;
    }

    /// <summary>Re-resolves the icon, kind label, and status line from the bound motion.</summary>
    public void Refresh()
    {
        if (_motion == null)
            return;

        if (!NameBox.IsFocused)
            NameBox.Text = _motion.EffectiveName;

        // Folders have no IconService glyph; they fall back to the vector silhouette.
        if (_motion is FolderMotion)
        {
            IconBinding.Set(IconImage, IconRequest.ForMotion(_motion),
                missing => FolderGlyph.Visibility = missing ? Visibility.Visible : Visibility.Collapsed);
        }
        else
        {
            FolderGlyph.Visibility = Visibility.Collapsed;
            IconBinding.Set(IconImage, IconRequest.ForMotion(_motion));
        }
        IconClearButton.Visibility = string.IsNullOrWhiteSpace(_motion.IconPath)
            ? Visibility.Collapsed
            : Visibility.Visible;

        string status = _motion.StatusText;
        StatusLine.Text = status.Length == 0 ? _motion.TypeLabel : $"{_motion.TypeLabel} · {status}";
        StatusLine.Foreground = _motion.IsConfigured ? StatusBrush : WarningBrush;
    }

    private void OnNameLostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading || _motion == null)
            return;

        string name = NameBox.Text.Trim();
        // An empty field — or one holding exactly the kind default — stores no custom
        // name, so defaults keep following the bound letter and the UI language.
        _motion.DisplayName = name.Length == 0 || name == _motion.DefaultName ? string.Empty : name;
        NameBox.Text = _motion.EffectiveName;
        Refresh();
        MotionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnBrowseIconClick(object sender, MouseButtonEventArgs e)
    {
        if (_motion != null)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = Loc.Get("Dialog_IconFilter"),
                Title = Loc.Get("Dialog_SelectCellIcon")
            };
            if (dialog.ShowDialog(Window.GetWindow(this)) == true)
            {
                _motion.IconPath = dialog.FileName;
                Refresh();
                MotionChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        e.Handled = true;
    }

    private void OnClearIconClick(object sender, MouseButtonEventArgs e)
    {
        if (_motion != null)
        {
            _motion.IconPath = string.Empty;
            Refresh();
            MotionChanged?.Invoke(this, EventArgs.Empty);
        }
        e.Handled = true;
    }

    private void OnDeleteClick(object sender, MouseButtonEventArgs e)
    {
        DeleteRequested?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }
}
