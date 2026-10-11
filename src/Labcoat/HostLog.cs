using System.Text;

namespace Labcoat;

/// <summary>
/// The host's log beyond the console: the explorer's log lines (the Output panel) and ASP.NET Core's (an
/// <see cref="ILoggerProvider"/>, filtered by the level of <c>--verbose</c> / <c>LABCOAT_LOG_LEVEL</c>), kept as the last
/// <see cref="RecentCapacity"/> lines for the diagnostics report and, with <c>--log-file</c>, appended to a file that is
/// rotated to <c>&lt;file&gt;.1</c> at <see cref="MaxFileBytes"/>. Thread safe.
/// </summary>
public sealed class HostLog : ILoggerProvider
{
    public const int RecentCapacity = 500;
    public const long MaxFileBytes = 10L * 1024 * 1024;
    /// <summary>The <c>--log-file</c> value for the default file: labcoat.log in the workspace data folder.</summary>
    public const string AutoFile = "auto";
    public const string DefaultFileName = "labcoat.log";
    public const string LevelVariable = "LABCOAT_LOG_LEVEL";
    public const string FileVariable = "LABCOAT_LOG_FILE";

    private readonly object _gate = new();
    private readonly Queue<string> _recent = new();
    private readonly long _maxFileBytes;
    private StreamWriter? _writer;
    private bool _fileFailed;

    /// <param name="level">Minimum level of the ASP.NET Core log (the explorer's own lines are always kept).</param>
    /// <param name="filePath">The log file (full path), or null for none.</param>
    public HostLog(LogLevel level = LogLevel.Warning, string? filePath = null, long maxFileBytes = MaxFileBytes)
    {
        Level = level;
        FilePath = filePath;
        _maxFileBytes = maxFileBytes;
    }

    public LogLevel Level { get; }
    public string? FilePath { get; }

    /// <summary>"trace", "debug", "information" / "info", "warning" / "warn", "error", "critical", "none" (any case); null otherwise.</summary>
    public static LogLevel? ParseLevel(string? value)
    {
        var v = value?.Trim() ?? "";
        if (v.Equals("info", StringComparison.OrdinalIgnoreCase)) return LogLevel.Information;
        if (v.Equals("warn", StringComparison.OrdinalIgnoreCase)) return LogLevel.Warning;
        // names only: Enum.TryParse also takes numbers ("3")
        return v.All(char.IsAsciiLetter) && Enum.TryParse<LogLevel>(v, ignoreCase: true, out var level) ? level : null;
    }

    /// <summary>The <c>--log-file</c> / <c>LABCOAT_LOG_FILE</c> value as a full path: <see cref="AutoFile"/> = labcoat.log in <paramref name="dataDirectory"/>.</summary>
    public static string ResolveFile(string value, string dataDirectory) =>
        value.Equals(AutoFile, StringComparison.OrdinalIgnoreCase) ? Path.Combine(dataDirectory, DefaultFileName) : Path.GetFullPath(value);

    /// <summary>An explorer log line (the Output panel's).</summary>
    public void Write(LogEvent e) => Append(e.Timestamp, e.Level, e.ProjectId is null ? e.Message : $"[{e.ProjectId}] {e.Message}");

    /// <summary>A line of <paramref name="level"/> ("info", "warning", …) at the current time.</summary>
    public void Write(string level, string message) => Append(Json.Now(), level, message);

    private void Append(string timestamp, string level, string message)
    {
        var line = $"{timestamp} {level,-7} {message}";
        lock (_gate)
        {
            _recent.Enqueue(line);
            while (_recent.Count > RecentCapacity) _recent.Dequeue();
            WriteFileLocked(line);
        }
    }

    /// <summary>The last <paramref name="count"/> lines, oldest first.</summary>
    public List<string> Recent(int count)
    {
        lock (_gate) return [.. _recent.Skip(Math.Max(0, _recent.Count - count))];
    }

    private void WriteFileLocked(string line)
    {
        if (FilePath is null || _fileFailed) return;
        try
        {
            _writer ??= Open(FilePath);
            if (_writer.BaseStream.Length + Encoding.UTF8.GetByteCount(line) + 1 > _maxFileBytes && _writer.BaseStream.Length > 0)
            {
                _writer.Dispose();
                File.Move(FilePath, FilePath + ".1", overwrite: true);
                _writer = Open(FilePath);
            }
            _writer.WriteLine(line);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Once: a log file that can't be written must not take the host down or flood the console.
            _fileFailed = true;
            Console.Error.WriteLine($"{Cli.Yellow("warning")} Could not write the log file {FilePath}: {ex.Message}");
        }
    }

    private static StreamWriter Open(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        return new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
    }

    /// <summary>The last <paramref name="count"/> lines of a log file (for <c>labcoat --diagnostics</c>, which has no running host).</summary>
    public static List<string> Tail(string file, int count)
    {
        try
        {
            if (!File.Exists(file)) return [];
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            const int window = 256 * 1024;
            if (stream.Length > window) stream.Seek(-window, SeekOrigin.End);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var lines = reader.ReadToEnd().Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToList();
            if (stream.Length > window && lines.Count > 0) lines.RemoveAt(0); // probably cut
            return lines.Skip(Math.Max(0, lines.Count - count)).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    // ------------------------------------------------------------------ ILoggerProvider (ASP.NET Core's log)

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    private sealed class Logger(HostLog log, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && logLevel >= log.Level;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var message = formatter(state, exception);
            if (exception is not null) message += Environment.NewLine + exception;
            log.Write(Name(logLevel), $"{category}: {message}");
        }

        private static string Name(LogLevel level) => level switch
        {
            LogLevel.Trace => "trace",
            LogLevel.Debug => "debug",
            LogLevel.Information => "info",
            LogLevel.Warning => "warning",
            LogLevel.Error => "error",
            _ => "critical",
        };
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }
}
