using Microsoft.Extensions.Logging;
using Nagi.Core.Services.Implementations.Presence;
using NSubstitute;
using Xunit;
using DiscordLogLevel = DiscordRPC.Logging.LogLevel;

namespace Nagi.Core.Tests.Presence;

public class DiscordLoggerAdapterTests
{
    [Fact]
    public void Warning_PreservesCompositeFormattingAndRepeatedArguments()
    {
        var logger = Substitute.For<ILogger>();
        logger.IsEnabled(Arg.Any<LogLevel>()).Returns(true);
        var adapter = new DiscordLoggerAdapter(logger);

        adapter.Warning("{1}: {0:X2} / {0:X2}", 15, "Value");

        logger.Received(1).Log(LogLevel.Warning, Arg.Any<EventId>(),
            Arg.Is<object>(state => state.ToString() == "[DiscordRPC] Value: 0F / 0F"),
            null, Arg.Any<Func<object, Exception?, string>>());
    }

    [Theory]
    [InlineData(DiscordLogLevel.Warning, 2)]
    [InlineData(DiscordLogLevel.None, 0)]
    public void Level_FiltersLibraryMessages(DiscordLogLevel minimum, int expected)
    {
        var logger = Substitute.For<ILogger>();
        logger.IsEnabled(Arg.Any<LogLevel>()).Returns(true);
        var adapter = new DiscordLoggerAdapter(logger) { Level = minimum };

        adapter.Trace("Trace");
        adapter.Info("Info");
        adapter.Warning("Warning");
        adapter.Error("Error");

        Assert.Equal(expected, logger.ReceivedCalls().Count(call => call.GetMethodInfo().Name == nameof(ILogger.Log)));
    }
}
