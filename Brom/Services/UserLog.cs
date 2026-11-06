using System;

namespace Brom.Services;

/// <summary>
/// Small helper that writes log lines a non-technical user can follow:
/// what the operation does, numbered steps, a clear result, and what to do
/// when something fails. All text goes through <see cref="ILogSink"/>, so it
/// works for both the WinForms log panel and headless callers.
/// </summary>
public static class UserLog
{
    public static void Banner(ILogSink log, string title, string whatItDoes = null)
    {
        if (log == null) return;

        log.LogInfo("═══════════════════════════════════════");
        log.LogInfo("  " + title);
        log.LogInfo("═══════════════════════════════════════");

        if (!string.IsNullOrWhiteSpace(whatItDoes))
        {
            log.LogInfo("What this does: " + whatItDoes);
        }
    }

    public static void Step(ILogSink log, int index, int total, string message)
    {
        log?.LogInfo($"Step {index}/{total}: {message}");
    }

    public static void Info(ILogSink log, string message)
    {
        log?.LogInfo(message);
    }

    public static void Success(ILogSink log, string message)
    {
        log?.LogSuccess("✔ " + message);
    }

    public static void Warn(ILogSink log, string message, params string[] whatToDo)
    {
        log?.LogWarning("! " + message);
        WriteHints(log, whatToDo);
    }

    public static void Fail(ILogSink log, string message, params string[] whatToDo)
    {
        log?.LogError("✖ " + message);
        WriteHints(log, whatToDo);
    }

    public static void Hints(ILogSink log, params string[] whatToDo)
    {
        WriteHints(log, whatToDo);
    }

    /// <summary>Renders a value for humans: null/empty becomes "not reported".</summary>
    public static string Display(string value)
    {
        return string.IsNullOrWhiteSpace(value) || value == "N/A" ? "not reported" : value;
    }

    private static void WriteHints(ILogSink log, string[] hints)
    {
        if (log == null || hints == null || hints.Length == 0) return;

        log.LogInfo("What to do:");
        foreach (string hint in hints)
        {
            if (!string.IsNullOrWhiteSpace(hint))
            {
                log.LogInfo("  • " + hint);
            }
        }
    }
}
