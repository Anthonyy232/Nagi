using System.Collections.Generic;
using System.Text;
using Serilog.Core;
using Serilog.Events;

namespace Nagi.Presentation.Logging;

/// <summary>
///     Keeps the most recent log events, oldest dropped first, so they can be attached to a crash report.
/// </summary>
internal sealed class MemoryLog : ILogEventSink
{
    private const int MaxEntries = 200;

    private readonly Lock _gate = new();
    private readonly Queue<LogEvent> _events = new(MaxEntries);

    private MemoryLog()
    {
    }

    public static MemoryLog Instance { get; } = new();

    public void Emit(LogEvent logEvent)
    {
        lock (_gate)
        {
            if (_events.Count == MaxEntries) _events.Dequeue();
            _events.Enqueue(logEvent);
        }
    }

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
