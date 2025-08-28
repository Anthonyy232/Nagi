using System;
using System.Collections.Concurrent;
using System.Text;
using Serilog.Core;
using Serilog.Events;

namespace Nagi.WinUI.Services.Implementations;

/// <summary>
///     A thread-safe Serilog sink that stores recent log events in memory.
///     This is useful for capturing logs for a crash report dialog.
/// </summary>
public class MemoryLog : ILogEventSink
{
    private const int MaxEntries = 200;

    private static readonly Lazy<MemoryLog> LazyInstance = new(() => new MemoryLog());
    private readonly ConcurrentQueue<LogEvent> _events = new();

    private MemoryLog()
    {
    }

    /// <summary>
    ///     Gets the singleton instance of the MemoryLog.
    /// </summary>
    public static MemoryLog Instance => LazyInstance.Value;

    /// <summary>
    ///     Emits a log event to the in-memory queue.
    ///     If the queue exceeds its maximum size, the oldest event is removed.
    /// </summary>
    public void Emit(LogEvent logEvent)
    {
        if (_events.Count >= MaxEntries) _events.TryDequeue(out _);
        _events.Enqueue(logEvent);
    }

    /// <summary>
    ///     Retrieves all stored log events as a single formatted string.
    /// </summary>
    /// <returns>A string containing all rendered log messages and exceptions.</returns>
    public string GetContent()
    {
        var sb = new StringBuilder();
        foreach (var ev in _events)
        {
            sb.AppendLine(ev.RenderMessage());
            if (ev.Exception != null) sb.AppendLine(ev.Exception.ToString());
        }

        return sb.ToString();
    }
}