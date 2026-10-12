using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
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

    private readonly Lock _gate = new();
    private readonly Queue<LogEvent> _events = new(MaxEntries);

    private MemoryLog()
    {
    }

    public static MemoryLog Instance { get; } = new();

    /// <summary>
    ///     Emits a log event to the in-memory queue.
    ///     If the queue exceeds its maximum size, the oldest event is removed.
    /// </summary>
    public void Emit(LogEvent logEvent)
    {
        lock (_gate)
        {
            if (_events.Count == MaxEntries) _events.Dequeue();
            _events.Enqueue(logEvent);
        }
    }

    /// <summary>
    ///     Retrieves all stored log events as a single formatted string.
    /// </summary>
    public string GetContent()
    {
        LogEvent[] events;
        lock (_gate) events = _events.ToArray();

        var sb = new StringBuilder();
        foreach (var ev in events)
        {
            sb.AppendLine(ev.RenderMessage());
            if (ev.Exception != null) sb.AppendLine(ev.Exception.ToString());
        }

        return sb.ToString();
    }
}
