using System.Text;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellySubSync;

/// <summary>
/// Per plugin log file: every log line (Debug and up) also goes to
/// &lt;jellyfin log dir&gt;/jellysubsync.log, next to the server log. The server log is unchanged.
/// Rolls over to jellysubsync.log.1 at 10 MB.
/// </summary>
internal static class PluginLog
{
    public const string FileName = "jellysubsync.log";

    private const long MaxBytes = 10L * 1024 * 1024;
    private const int MaxPending = 1000;

    private static readonly object Gate = new();
    private static readonly Queue<string> Pending = new();
    private static string? _path;

    /// <summary>Full path of the log file, null until the plugin is constructed.</summary>
    public static string? FilePath => _path;

    /// <summary>Called from the Plugin constructor. Lines logged before that are kept and flushed here.</summary>
    public static void Init(string logDirectory)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(logDirectory);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            _path = Path.Combine(logDirectory, FileName);
            if (Pending.Count > 0)
            {
                var sb = new StringBuilder();
                while (Pending.Count > 0)
                {
                    sb.Append(Pending.Dequeue());
                }

                Append(sb.ToString());
            }
        }
    }

    public static ILogger<T> Wrap<T>(ILogger<T> inner) => new TeeLogger<T>(inner);

    /// <summary>A line only in the plugin log file (no server logger at hand, e.g. in the Plugin constructor).</summary>
    public static void Note(string category, string message) => Write(LogLevel.Information, category, message, null);

    private static void Write(LogLevel level, string category, string message, Exception? ex)
    {
        var line = new StringBuilder()
            .Append('[').Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz")).Append("] [")
            .Append(Abbrev(level)).Append("] [").Append(Environment.CurrentManagedThreadId).Append("] ")
            .Append(category).Append(": ").Append(message).AppendLine();
        if (ex is not null)
        {
            line.Append(ex).AppendLine();
        }

        lock (Gate)
        {
            if (_path is null)
            {
                if (Pending.Count >= MaxPending)
                {
                    Pending.Dequeue();
                }

                Pending.Enqueue(line.ToString());
                return;
            }

            Append(line.ToString());
        }
    }

    // caller holds Gate
    private static void Append(string text)
    {
        try
        {
            var fi = new FileInfo(_path!);
            if (fi.Exists && fi.Length > MaxBytes)
            {
                File.Move(_path!, _path + ".1", true);
            }

            File.AppendAllText(_path!, text);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string Abbrev(LogLevel level) => level switch
    {
        LogLevel.Trace => "VRB",
        LogLevel.Debug => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        LogLevel.Critical => "FTL",
        _ => "???"
    };

    private sealed class TeeLogger<T> : ILogger<T>
    {
        private static readonly string Category = typeof(T).FullName ?? typeof(T).Name;
        private readonly ILogger<T> _inner;

        public TeeLogger(ILogger<T> inner)
        {
            _inner = inner;
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => _inner.BeginScope(state);

        // file takes Debug and up even when the server log is at Information
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug && logLevel != LogLevel.None || _inner.IsEnabled(logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            _inner.Log(logLevel, eventId, state, exception, formatter);
            if (logLevel >= LogLevel.Debug && logLevel != LogLevel.None)
            {
                Write(logLevel, Category, formatter(state, exception), exception);
            }
        }
    }
}
