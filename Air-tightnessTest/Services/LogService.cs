using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace LumbarMassageTest.Services
{
    public interface ILogService
    {
        event EventHandler<LogEventArgs>? LogReceived;

        void LogInfo(string message);
        void LogWarning(string message);
        void LogWarning(string message, Exception? exception);
        void LogError(string message);
        void LogError(string message, Exception? exception);
        IReadOnlyCollection<LogEntry> GetRecentEntries();
        void ClearLogs();
    }

    public sealed class LogService : ILogService
    {
        private static readonly Lazy<LogService> _lazyInstance = new(() => new LogService());
        public static LogService Instance => _lazyInstance.Value;

        private readonly string _logDirectory;
        private readonly ReaderWriterLockSlim _fileLock = new();
        private readonly ConcurrentQueue<LogEntry> _recentEntries = new();
        private const int MaxCachedEntries = 500;

        public event EventHandler<LogEventArgs>? LogReceived;

        private LogService()
        {
            _logDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs");
            if (!Directory.Exists(_logDirectory))
            {
                Directory.CreateDirectory(_logDirectory);
            }

            LoadExistingEntries();
        }

        public void LogInfo(string message) => WriteLog(LogLevel.Info, message, null);

        public void LogWarning(string message) => WriteLog(LogLevel.Warning, message, null);

        public void LogWarning(string message, Exception? exception) => WriteLog(LogLevel.Warning, message, exception);

        public void LogError(string message) => WriteLog(LogLevel.Error, message, null);

        public void LogError(string message, Exception? exception) => WriteLog(LogLevel.Error, message, exception);

        private void WriteLog(LogLevel level, string message, Exception? exception)
        {
            var entry = new LogEntry(DateTime.Now, level, message, exception);
            try
            {
                var logFile = Path.Combine(_logDirectory, $"System_{DateTime.Now:yyyyMMdd}.log");
                var builder = new StringBuilder();
                builder.Append('[')
                       .Append(entry.Timestamp.ToString("yyyy-MM-dd HH:mm:ss"))
                       .Append("] [")
                       .Append(entry.Level)
                       .Append("] ")
                       .Append(entry.Message);
                if (entry.Exception != null)
                {
                    builder.Append(" | Exception: ").Append(entry.Exception);
                }

                var line = builder.ToString();
                WriteLineSafe(logFile, line);
            }
            catch
            {
                // Ignore logging failures
            }
            finally
            {
                _recentEntries.Enqueue(entry);
                TrimCache();

                LogReceived?.Invoke(this, new LogEventArgs(entry));
            }
        }

        public IReadOnlyCollection<LogEntry> GetRecentEntries()
        {
            return _recentEntries.ToArray();
        }

        public void ClearLogs()
        {
            while (_recentEntries.TryDequeue(out _))
            {
                // discard cached entries
            }

            try
            {
                _fileLock.EnterWriteLock();
                try
                {
                    foreach (var file in Directory.EnumerateFiles(_logDirectory, "*.log"))
                    {
                        File.Delete(file);
                    }
                }
                finally
                {
                    _fileLock.ExitWriteLock();
                }
            }
            catch
            {
                // Ignore cleanup failures to keep UI responsive
            }
        }

        private void LoadExistingEntries()
        {
            try
            {
                var logFile = Path.Combine(_logDirectory, $"System_{DateTime.Now:yyyyMMdd}.log");
                if (!File.Exists(logFile))
                {
                    return;
                }

                foreach (var line in File.ReadLines(logFile))
                {
                    if (TryParseLogLine(line, out var entry))
                    {
                        _recentEntries.Enqueue(entry);
                        TrimCache();
                    }
                }
            }
            catch
            {
                // Ignore load errors
            }
        }

        private static bool TryParseLogLine(string line, out LogEntry entry)
        {
            entry = null!;
            if (string.IsNullOrWhiteSpace(line) || line[0] != '[')
            {
                return false;
            }

            var firstEnd = line.IndexOf(']');
            if (firstEnd <= 1)
            {
                return false;
            }

            var secondStart = line.IndexOf('[', firstEnd + 1);
            if (secondStart < 0)
            {
                return false;
            }

            var secondEnd = line.IndexOf(']', secondStart + 1);
            if (secondEnd < 0)
            {
                return false;
            }

            var timestampText = line.Substring(1, firstEnd - 1);
            if (!DateTime.TryParse(timestampText, out var timestamp))
            {
                return false;
            }

            var levelText = line.Substring(secondStart + 1, secondEnd - secondStart - 1);
            if (!Enum.TryParse(levelText, out LogLevel level))
            {
                level = LogLevel.Info;
            }

            var messageStart = secondEnd + 2; // skip trailing space
            if (messageStart > line.Length)
            {
                messageStart = line.Length;
            }

            var message = line.Substring(messageStart);

            entry = new LogEntry(timestamp, level, message, null);
            return true;
        }

        private void TrimCache()
        {
            while (_recentEntries.Count > MaxCachedEntries && _recentEntries.TryDequeue(out _))
            {
                // keep queue bounded
            }
        }

        private void WriteLineSafe(string path, string line)
        {
            _fileLock.EnterWriteLock();
            try
            {
                File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
            }
            finally
            {
                _fileLock.ExitWriteLock();
            }
        }
    }

    public record LogEntry(DateTime Timestamp, LogLevel Level, string Message, Exception? Exception);

    public enum LogLevel
    {
        Info,
        Warning,
        Error
    }

    public sealed class LogEventArgs : EventArgs
    {
        public LogEventArgs(LogEntry entry)
        {
            Entry = entry;
        }

        public LogEntry Entry { get; }
    }
}
