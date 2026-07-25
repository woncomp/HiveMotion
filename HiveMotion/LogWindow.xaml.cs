using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using HiveMotion.Localization;

namespace HiveMotion;

public partial class LogWindow : Window
{
    private readonly ObservableCollection<LogEntry> _entries = new();
    private bool _isInitialized;

    public LogWindow()
    {
        InitializeComponent();
        foreach (LogEntry entry in Logger.GetSessionEntries())
            _entries.Add(entry);

        PathText.Text = Logger.ActiveLogPath;
        Logger.EntryWritten += OnLogEntryWritten;
        LocalizationManager.Instance.CultureChanged += OnCultureChanged;
        Closed += OnClosed;
        _isInitialized = true;
        RefreshOutput();
    }

    private void OnLogEntryWritten(object? sender, LogEntry entry)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _entries.Add(entry);
            PathText.Text = Logger.ActiveLogPath;
            RefreshOutput();
        });
    }

    private bool IsEntryVisible(LogEntry entry) =>
        IsLevelEnabled(entry.Level) &&
        IsChannelEnabled(entry.Channel) &&
        (string.IsNullOrWhiteSpace(SearchInput.Text) ||
         entry.DisplayText.Contains(SearchInput.Text, StringComparison.OrdinalIgnoreCase));

    private bool IsLevelEnabled(LogLevel level) => level switch
    {
        LogLevel.Info => InfoFilter.IsChecked == true,
        LogLevel.Warning => WarningFilter.IsChecked == true,
        LogLevel.Error => ErrorFilter.IsChecked == true,
        _ => false
    };

    private bool IsChannelEnabled(LogChannel channel) => ChannelFilter.SelectedIndex switch
    {
        1 => channel == LogChannel.Default,
        2 => channel == LogChannel.Activation,
        _ => true
    };

    private void OnFilterChanged(object sender, RoutedEventArgs e)
    {
        if (_isInitialized)
            RefreshOutput();
    }

    private void OnFollowChanged(object sender, RoutedEventArgs e)
    {
        if (_isInitialized && FollowToggle.IsChecked == true)
            LogOutput.ScrollToEnd();
    }

    private void RefreshOutput()
    {
        LogOutput.Text = string.Join(Environment.NewLine, _entries.Where(IsEntryVisible).Select(entry => entry.DisplayText));
        UpdateEntryCount();
        if (FollowToggle.IsChecked == true)
            LogOutput.ScrollToEnd();
    }

    private void OnCopySelectedClick(object sender, RoutedEventArgs e) =>
        CopyText(LogOutput.SelectedText);

    private void OnCopyVisibleClick(object sender, RoutedEventArgs e) =>
        CopyText(string.Join(Environment.NewLine, _entries.Where(IsEntryVisible).Select(entry => entry.DisplayText)));

    private static void CopyText(string text)
    {
        if (text.Length == 0)
            return;

        try
        {
            Clipboard.SetText(text);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Copying log entries to the clipboard");
        }
    }

    private void OnClearViewClick(object sender, RoutedEventArgs e)
    {
        _entries.Clear();
        RefreshOutput();
    }

    private void OnOpenFolderClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = Logger.LogDirectoryPath,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Opening the log folder");
        }
    }

    private void OnCultureChanged(object? sender, EventArgs e)
    {
        if (_isInitialized)
            UpdateEntryCount();
    }

    private void UpdateEntryCount()
    {
        int visibleCount = _entries.Count(IsEntryVisible);
        EntryCountText.Text = Loc.Plural("Log_EntryCount", visibleCount, visibleCount);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        Logger.EntryWritten -= OnLogEntryWritten;
        LocalizationManager.Instance.CultureChanged -= OnCultureChanged;
    }
}
