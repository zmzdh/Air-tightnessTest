using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LumbarMassageTest.Services;

namespace LumbarMassageTest.UserControls
{
    public partial class SystemLogControl : UserControl
    {
        private readonly ILogService _logService;
        private readonly ObservableCollection<LogEntryViewModel> _entries = new();
        private LogLevel? _currentFilter;

        public SystemLogControl()
        {
            InitializeComponent();
            _logService = LogService.Instance;
            LogItemsControl.ItemsSource = _entries;

            Loaded += SystemLogControl_Loaded;
            Unloaded += SystemLogControl_Unloaded;
        }

        private void SystemLogControl_Loaded(object sender, RoutedEventArgs e)
        {
            _logService.LogReceived -= LogService_LogReceived;
            _logService.LogReceived += LogService_LogReceived;
            LoadInitialEntries();
        }

        private void SystemLogControl_Unloaded(object sender, RoutedEventArgs e)
        {
            _logService.LogReceived -= LogService_LogReceived;
        }

        private void LoadInitialEntries()
        {
            _entries.Clear();

            var recentEntries = _logService?.GetRecentEntries() ?? Array.Empty<LogEntry>();

            var orderedEntries = recentEntries
                .Where(e => e != null)
                .Select(e => e!)
                .OrderBy(e => e.Timestamp);

            foreach (var entry in orderedEntries)
            {
                if (ShouldInclude(entry.Level))
                {
                    AddOrUpdateEntry(entry);
                }
            }

            ScrollToEnd();
        }

        private void LogService_LogReceived(object? sender, LogEventArgs e)
        {
            if (!ShouldInclude(e.Entry.Level))
            {
                return;
            }

            Dispatcher.Invoke(() =>
            {
                AddOrUpdateEntry(e.Entry);
                if (_entries.Count > 1000)
                {
                    while (_entries.Count > 800)
                    {
                        _entries.RemoveAt(0);
                    }
                }

                ScrollToEnd();
            });
        }

        private void AddOrUpdateEntry(LogEntry entry)
        {
            var viewModel = LogEntryViewModel.FromEntry(entry);
            var existingIndex = FindEntryIndex(viewModel.Level, viewModel.Message);
            if (existingIndex >= 0)
            {
                _entries.RemoveAt(existingIndex);
            }

            _entries.Add(viewModel);
        }

        private int FindEntryIndex(LogLevel level, string message)
        {
            for (int i = 0; i < _entries.Count; i++)
            {
                var entry = _entries[i];
                if (entry.Level == level && string.Equals(entry.Message, message, StringComparison.Ordinal))
                {
                    return i;
                }
            }

            return -1;
        }

        private bool ShouldInclude(LogLevel level)
        {
            if (_currentFilter == null)
            {
                return true;
            }

            return level == _currentFilter;
        }

        private void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            LoadInitialEntries();
        }

        private void ClearButton_Click(object sender, RoutedEventArgs e)
        {
            _logService.ClearLogs();
            _entries.Clear();
        }

        private void LevelFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (LevelFilter.SelectedItem is ComboBoxItem item && item.Tag is string tag)
            {
                _currentFilter = tag switch
                {
                    "Info" => LogLevel.Info,
                    "Warning" => LogLevel.Warning,
                    "Error" => LogLevel.Error,
                    _ => null
                };
                LoadInitialEntries();
            }
        }

        private void ScrollToEnd()
        {
            if (LogItemsControl == null || LogScrollViewer == null)
            {
                return;
            }

            if (_entries.Count == 0)
            {
                return;
            }

            var last = _entries[^1];

            if (!LogItemsControl.IsLoaded)
            {
                LogItemsControl.Loaded -= LogItemsControlOnLoaded;
                LogItemsControl.Loaded += LogItemsControlOnLoaded;
                return;
            }

            LogItemsControl.Dispatcher.InvokeAsync(() =>
            {
                if (LogItemsControl.ItemContainerGenerator.ContainerFromItem(last) is FrameworkElement element)
                {
                    element.BringIntoView();
                }
                else
                {
                    LogScrollViewer.ScrollToEnd();
                }
            });
        }

        private void LogItemsControlOnLoaded(object sender, RoutedEventArgs e)
        {
            LogItemsControl.Loaded -= LogItemsControlOnLoaded;
            ScrollToEnd();
        }

        private class LogEntryViewModel
        {
            private LogEntryViewModel(DateTime timestamp, LogLevel level, string message)
            {
                Timestamp = timestamp;
                Level = level;
                Message = message;
            }

            public DateTime Timestamp { get; }
            public LogLevel Level { get; }
            public string Message { get; }

            public Brush LevelColor => Level switch
            {
                LogLevel.Error => Brushes.Red,
                LogLevel.Warning => Brushes.DarkOrange,
                _ => Brushes.SteelBlue
            };

            public static LogEntryViewModel FromEntry(LogEntry entry)
            {
                return new LogEntryViewModel(entry.Timestamp, entry.Level, BuildMessage(entry));
            }
        }

        private static string BuildMessage(LogEntry entry)
        {
            var message = entry.Message;
            if (entry.Exception != null)
            {
                message += $" | {entry.Exception.Message}";
            }

            return message;
        }
    }
}
