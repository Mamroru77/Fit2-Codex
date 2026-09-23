using System.Text;
using CodexQuota.Core.Logging;
using Microsoft.Extensions.Logging;

namespace CodexQuota.Desktop.Logging;

/// <summary>
/// File logger that redacts every line before it reaches disk.
/// </summary>
/// <remarks>
/// Redaction happens once, on the fully formatted line, so the message text, the structured
/// state and the exception trace are all covered by the same pass. There is no code path that
/// writes to the log file without going through <see cref="SensitiveDataRedactor.Redact"/>.
/// </remarks>
public sealed class RedactingFileLoggerProvider : ILoggerProvider
{
    /// <summary>Maximum size of one log file before it is rotated out.</summary>
    public const long MaxFileSizeBytes = 5L * 1024 * 1024;

    /// <summary>Maximum number of log files kept, counting the one currently being written.</summary>
    public const int MaxFileCount = 5;

    private const string CurrentFileName = "bridge.log";

    private readonly string _logDirectory;
    private readonly long _maxFileSizeBytes;
    private readonly int _maxFileCount;
    private readonly object _gate = new();

    private StreamWriter? _writer;
    private bool _disposed;

    public RedactingFileLoggerProvider(
        string logDirectory,
        long maxFileSizeBytes = MaxFileSizeBytes,
        int maxFileCount = MaxFileCount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logDirectory);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxFileSizeBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxFileCount);

        _logDirectory = logDirectory;
        _maxFileSizeBytes = maxFileSizeBytes;
        _maxFileCount = maxFileCount;
    }

    /// <summary>Directory the log files are written to.</summary>
    public string LogDirectory => _logDirectory;

    public ILogger CreateLogger(string categoryName) => new RedactingFileLogger(this, categoryName);

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _writer?.Flush();
            _writer?.Dispose();
            _writer = null;
        }
    }

    internal void WriteLine(string line)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _writer ??= OpenWriter();
            _writer.WriteLine(line);
            _writer.Flush();

            if (_writer.BaseStream.Length >= _maxFileSizeBytes)
            {
                Rotate();
            }
        }
    }

    private StreamWriter OpenWriter()
    {
        Directory.CreateDirectory(_logDirectory);

        var stream = new FileStream(CurrentPath, FileMode.Append, FileAccess.Write, FileShare.Read);

        return new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = false };
    }

    private void Rotate()
    {
        _writer?.Dispose();
        _writer = null;

        // Shift oldest-first so nothing is overwritten: bridge.log -> bridge.1.log -> ...
        for (var index = _maxFileCount - 1; index >= 1; index--)
        {
            var source = index == 1 ? CurrentPath : RotatedPath(index - 1);
            var target = RotatedPath(index);

            if (!File.Exists(source))
            {
                continue;
            }

            if (File.Exists(target))
            {
                File.Delete(target);
            }

            File.Move(source, target);
        }

        _writer = OpenWriter();
    }

    private string CurrentPath => Path.Combine(_logDirectory, CurrentFileName);

    private string RotatedPath(int index) => Path.Combine(_logDirectory, $"bridge.{index}.log");

    private sealed class RedactingFileLogger : ILogger
    {
        private readonly RedactingFileLoggerProvider _owner;
        private readonly string _category;

        internal RedactingFileLogger(RedactingFileLoggerProvider owner, string category)
        {
            _owner = owner;
            _category = category;
        }

        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var line = $"{DateTimeOffset.UtcNow:O} [{logLevel}] {_category}: {formatter(state, exception)}";

            if (exception is not null)
            {
                line += Environment.NewLine + exception;
            }

            _owner.WriteLine(SensitiveDataRedactor.Redact(line));
        }
    }

    private sealed class NullScope : IDisposable
    {
        internal static readonly NullScope Instance = new();

        public void Dispose()
        {
        }
    }
}
