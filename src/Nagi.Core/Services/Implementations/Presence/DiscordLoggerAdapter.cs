using DiscordRPC.Logging;
using Microsoft.Extensions.Logging;
using LogLevel = DiscordRPC.Logging.LogLevel;

namespace Nagi.Core.Services.Implementations.Presence;

public class DiscordLoggerAdapter : DiscordRPC.Logging.ILogger
{
    private readonly Microsoft.Extensions.Logging.ILogger _logger;

    public DiscordLoggerAdapter(Microsoft.Extensions.Logging.ILogger logger)
    {
        _logger = logger;
    }

    // Only surface warnings and errors from the library by default; internal chatter is very verbose.
    public LogLevel Level { get; set; } = LogLevel.Warning;

    public void Trace(string message, params object[] args) =>
        _logger.LogTrace("[DiscordRPC] " + message, args);

    public void Info(string message, params object[] args) =>
        _logger.LogInformation("[DiscordRPC] " + message, args);

    public void Warning(string message, params object[] args) =>
        _logger.LogWarning("[DiscordRPC] " + message, args);

    public void Error(string message, params object[] args) =>
        _logger.LogError("[DiscordRPC] " + message, args);
}
