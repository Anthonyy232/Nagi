using FluentAssertions;
using Nagi.Presentation.Logging;
using Serilog.Events;
using Serilog.Parsing;
using Xunit;

namespace Nagi.Presentation.Tests;

public class MemoryLogTests
{
    [Fact]
    public void ConcurrentLogging_RemainsBounded_AndRetainsLatestEventsInOrder()
    {
        var sink = MemoryLog.Instance;
        var parser = new MessageTemplateParser();
        var message = new LogEvent(DateTimeOffset.UtcNow, LogEventLevel.Information, null,
            parser.Parse("Concurrent entry"), []);

        Parallel.For(0, 10000, _ => sink.Emit(message));

        sink.GetContent().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .Should().HaveCount(200);

        for (var i = 0; i <= 200; i++)
            sink.Emit(new LogEvent(DateTimeOffset.UtcNow, LogEventLevel.Information, null,
                parser.Parse($"Entry {i}"), []));

        sink.GetContent().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .Should().Equal(Enumerable.Range(1, 200).Select(i => $"Entry {i}"));
    }
}
