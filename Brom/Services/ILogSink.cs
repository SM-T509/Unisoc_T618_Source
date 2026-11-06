using System;
using System.Threading;

namespace Brom.Services;

public enum LogType
{
    Info,
    Success,
    Warning,
    Error,
    Debug
}

public interface ILogSink
{
    void Log(string message, LogType type = LogType.Info);
    void LogInfo(string message) => Log(message, LogType.Info);
    void LogSuccess(string message) => Log(message, LogType.Success);
    void LogWarning(string message) => Log(message, LogType.Warning);
    void LogError(string message) => Log(message, LogType.Error);
    void LogDebug(string message) => Log(message, LogType.Debug);
}

public sealed class NullLogSink : ILogSink
{
    public static readonly NullLogSink Instance = new();
    private NullLogSink() { }
    public void Log(string message, LogType type = LogType.Info) { }
}

public delegate void LogAction(string message, LogType type);

public sealed class DelegatedLogSink : ILogSink
{
    private readonly Action<string, LogType> _action;
    private readonly SynchronizationContext _context;

    public DelegatedLogSink(Action<string, LogType> action, bool captureContext = true)
    {
        _action = action ?? throw new ArgumentNullException(nameof(action));
        _context = captureContext ? SynchronizationContext.Current : null;
    }

    public void Log(string message, LogType type = LogType.Info)
    {
        if (_context != null)
        {
            _context.Post(_ => _action(message, type), null);
        }
        else
        {
            _action(message, type);
        }
    }
}

public sealed class CompositeLogSink : ILogSink
{
    private readonly ILogSink[] _sinks;

    public CompositeLogSink(params ILogSink[] sinks)
    {
        _sinks = sinks ?? Array.Empty<ILogSink>();
    }

    public void Log(string message, LogType type = LogType.Info)
    {
        foreach (var sink in _sinks)
        {
            try { sink.Log(message, type); } catch { }
        }
    }
}
