using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Brom.Services;

/// <summary>
/// Structured, timestamped diagnostics for support logs.
///
/// The sidebar log stays human-readable (and the 43-column T509 blocks must not
/// be widened), so the machine-readable record is written to a separate rolling
/// file next to the application:
///
/// <code>
/// [2026-10-27 14:03:11.482] [INFO ] [T509 ] [Handshake] op=3f9a1c2d t=+0.000s operation started
/// [2026-10-27 14:03:11.483] [DEBUG] [T509 ] [Handshake] op=3f9a1c2d t=+0.001s port=COM21
/// [2026-10-27 14:03:29.117] [INFO ] [T509 ] [Handshake] op=3f9a1c2d t=+17.635s operation completed status=ok
/// </code>
///
/// Every line carries a timestamp, a severity, the tab, the component, a
/// correlation id shared by the whole operation, and the elapsed time. Secrets
/// (passwords, tokens, keys) are redacted and long values are truncated, so a
/// log can be attached to a bug report as-is.
/// </summary>
public static class OpLog
{
    private const string Area = "Brom";
    private const int MaxMessageLength = 4000;
    private const int MaxStackTraceLines = 12;

    private static readonly object _gate = new();
    private static readonly Regex _secret = new(
        @"(?<key>pass(?:word|wd)?|pwd|pin|token|secret|api[_-]?key|private[_-]?key|auth)\s*[:=]\s*(""[^""]*""|'[^']*'|[^\s,;]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static string _logDirectory;
    private static bool _disabled;

    /// <summary>Directory that receives <c>app-yyyyMMdd.log</c>.</summary>
    public static string LogDirectory
    {
        get
        {
            if (_logDirectory != null) return _logDirectory;

            string[] candidates =
            {
                Path.Combine(PathManager.Instance.BaseDirectory, "logs"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Area, "logs"),
                Path.Combine(Path.GetTempPath(), Area, "logs"),
            };

            foreach (string dir in candidates)
            {
                try
                {
                    Directory.CreateDirectory(dir);
                    _logDirectory = dir;
                    return dir;
                }
                catch
                {
                    // Try the next candidate: a read-only install directory must
                    // not stop the app from logging.
                }
            }

            _disabled = true;
            _logDirectory = candidates[candidates.Length - 1];
            return _logDirectory;
        }
    }

    public static string CurrentLogFile =>
        Path.Combine(LogDirectory, "app-" + DateTime.Now.ToString("yyyyMMdd") + ".log");

    /// <summary>Starts a correlated operation scope. Dispose it (or call
    /// <see cref="OpScope.Complete"/>) to record the final status and elapsed
    /// time. Never throws.</summary>
    public static OpScope Begin(string tab, string component, string operation)
        => new(tab, component, operation);

    public static void Info(string tab, string component, string message) =>
        Write("INFO ", tab, component, null, 0, message);

    public static void Debug(string tab, string component, string message) =>
        Write("DEBUG", tab, component, null, 0, message);

    public static void Warn(string tab, string component, string message) =>
        Write("WARN ", tab, component, null, 0, message);

    public static void Error(string tab, string component, string message) =>
        Write("ERROR", tab, component, null, 0, message);

    /// <summary>Writes the type, message and stack trace of an exception. The
    /// text is redacted and trimmed to a readable length.</summary>
    public static void Exception(string tab, string component, Exception ex, string context = null)
        => Exception(tab, component, null, 0, ex, context);

    /// <summary>Same, but keeps the correlation id of the running operation so
    /// the stack trace can be matched to the rest of the log lines.</summary>
    internal static void Exception(string tab, string component, string operationId,
        long elapsedMs, Exception ex, string context = null)
    {
        if (ex == null) return;

        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(context))
        {
            sb.Append(context).Append(": ");
        }
        sb.Append(ex.GetType().FullName).Append(": ").Append(ex.Message);

        Exception inner = ex.InnerException;
        int depth = 0;
        while (inner != null && depth++ < 3)
        {
            sb.Append(" <- ").Append(inner.GetType().Name).Append(": ").Append(inner.Message);
            inner = inner.InnerException;
        }

        if (!string.IsNullOrWhiteSpace(ex.StackTrace))
        {
            string[] lines = ex.StackTrace.Split('\n');
            int count = Math.Min(lines.Length, MaxStackTraceLines);
            for (int i = 0; i < count; i++)
            {
                sb.Append(Environment.NewLine).Append("    ").Append(lines[i].TrimEnd());
            }
            if (lines.Length > count)
            {
                sb.Append(Environment.NewLine).Append("    ... ").Append(lines.Length - count).Append(" more frames");
            }
        }

        Write("ERROR", tab, component, operationId, elapsedMs, sb.ToString());
    }

    internal static void Write(string level, string tab, string component, string operationId,
        long elapsedMs, string message)
    {
        if (_disabled) return;

        string line = Format(level, tab, component, operationId, elapsedMs, message);

        lock (_gate)
        {
            try
            {
                File.AppendAllText(CurrentLogFile, line + Environment.NewLine, Encoding.UTF8);
            }
            catch
            {
                // Diagnostics must never take the operation down. Try the
                // fallback location once, then stay quiet.
                try
                {
                    _disabled = true;
                }
                catch { }
            }
        }
    }

    private static string Format(string level, string tab, string component, string operationId,
        long elapsedMs, string message)
    {
        string safeMessage = Sanitize(message);
        string stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
        string tabText = Pad(tab, 6);
        string componentText = Pad(component, 14);

        string correlation = string.IsNullOrEmpty(operationId)
            ? "op=-------- t=--------"
            : "op=" + operationId + " t=+" + (elapsedMs / 1000.0).ToString("0.000") + "s";

        return $"[{stamp}] [{level}] [{tabText}] [{componentText}] {correlation}  {safeMessage}";
    }

    private static string Pad(string value, int width)
    {
        string text = string.IsNullOrWhiteSpace(value) ? "-" : value.Trim();
        return text.Length >= width ? text.Substring(0, width) : text.PadRight(width);
    }

    private static string Sanitize(string message)
    {
        if (string.IsNullOrEmpty(message)) return string.Empty;

        string result;
        try
        {
            result = _secret.Replace(message, m => m.Groups["key"].Value + "=***");
        }
        catch
        {
            result = message;
        }

        if (result.Length > MaxMessageLength)
        {
            result = result.Substring(0, MaxMessageLength) + "... (truncated)";
        }

        return result.Replace("\r\n", " ").Replace("\n", " ");
    }

    /// <summary>A single correlated operation. Use <c>using var op = OpLog.Begin(...)</c>;
    /// <see cref="Complete"/> records the outcome, <see cref="Dispose"/> records
    /// an abandoned scope as a failure.</summary>
    public sealed class OpScope : IDisposable
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly string _tab;
        private readonly string _component;
        private readonly string _operation;
        private bool _finished;

        internal OpScope(string tab, string component, string operation)
        {
            _tab = tab;
            _component = component;
            _operation = operation;
            Id = Guid.NewGuid().ToString("N").Substring(0, 8);
            Info("operation started (" + operation + ")");
        }

        /// <summary>Correlation id shared by every line of this operation.</summary>
        public string Id { get; }

        public long ElapsedMs => _clock.ElapsedMilliseconds;

        public void Stage(string stage) => Info("stage: " + stage);

        public void Info(string message) => Write("INFO ", _tab, _component, Id, ElapsedMs, message);

        public void Debug(string message) => Write("DEBUG", _tab, _component, Id, ElapsedMs, message);

        public void Warn(string message) => Write("WARN ", _tab, _component, Id, ElapsedMs, message);

        public void Error(string message) => Write("ERROR", _tab, _component, Id, ElapsedMs, message);

        public void Exception(Exception ex, string context = null) =>
            Services.OpLog.Exception(_tab, _component, Id, ElapsedMs, ex, context);

        /// <summary>Records the final status exactly once.</summary>
        public void Complete(bool ok, string detail = null)
        {
            if (_finished) return;
            _finished = true;

            string suffix = string.IsNullOrWhiteSpace(detail) ? string.Empty : " " + detail;
            if (ok)
            {
                Write("INFO ", _tab, _component, Id, ElapsedMs, "operation completed status=ok" + suffix);
            }
            else
            {
                Write("ERROR", _tab, _component, Id, ElapsedMs, "operation failed status=failed" + suffix);
            }
        }

        public void Dispose()
        {
            if (_finished) return;
            Complete(false, "status=abandoned");
        }
    }
}
