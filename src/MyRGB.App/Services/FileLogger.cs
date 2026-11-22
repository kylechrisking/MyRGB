using Microsoft.Extensions.Logging;

namespace MyRGB.App.Services;

/// <summary>Appends log lines to %APPDATA%\MyRGB\logs\myrgb.log, rolling to .old past 2 MB.</summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private const long MaxBytes = 2 * 1024 * 1024;
    private readonly object _gate = new();
    private readonly string _path;

    public FileLoggerProvider(string path)
    {
        _path = path;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    }

    public string FilePath => _path;

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose() { }

    /// <summary>Why the log file can't be written, while it can't; lines then go to <see cref="FallbackPath"/>.</summary>
    public string? WriteError { get; private set; }

    /// <summary>Where lines go when the log file refuses them: %TEMP%\MyRGB\myrgb.log.</summary>
    public static string FallbackPath { get; } = Path.Combine(Path.GetTempPath(), "MyRGB", "myrgb.log");

    private void Write(string line)
    {
        lock (_gate)
        {
            try
            {
                var info = new FileInfo(_path);
                if (info.Exists && info.Length > MaxBytes) File.Move(_path, _path + ".old", overwrite: true);
                File.AppendAllText(_path, line + Environment.NewLine);
                WriteError = null;
            }
            catch (Exception ex)
            {
                // Logging must never take the app down, but it mustn't vanish either: from 11-18 to 11-22 an
                // instance wrote nothing at all and nobody could tell why. Say why, somewhere else.
                bool first = WriteError is null;
                WriteError = $"{ex.GetType().Name}: {ex.Message}";
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(FallbackPath)!);
                    if (first)
                        File.AppendAllText(FallbackPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} Warning     FileLogger: can't write {_path}: {WriteError}{Environment.NewLine}");
                    File.AppendAllText(FallbackPath, line + Environment.NewLine);
                }
                catch { /* nowhere left to write */ }
            }
        }
    }

    private sealed class FileLogger(FileLoggerProvider owner, string category) : ILogger
    {
        private readonly string _category = category[(category.LastIndexOf('.') + 1)..];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) =>
            logLevel >= (category.StartsWith("Microsoft", StringComparison.Ordinal) ? LogLevel.Warning : LogLevel.Information);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {logLevel,-11} {_category}: {formatter(state, exception)}";
            if (exception is not null) line += Environment.NewLine + exception;
            owner.Write(line);
        }
    }
}
