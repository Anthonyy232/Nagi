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
        Log(LogLevel.Trace, Microsoft.Extensions.Logging.LogLevel.Trace, message, args);

    public void Info(string message, params object[] args) =>
        Log(LogLevel.Info, Microsoft.Extensions.Logging.LogLevel.Information, message, args);

    public void Warning(string message, params object[] args) =>
        Log(LogLevel.Warning, Microsoft.Extensions.Logging.LogLevel.Warning, message, args);

    public void Error(string message, params object[] args) =>
        Log(LogLevel.Error, Microsoft.Extensions.Logging.LogLevel.Error, message, args);

    private void Log(LogLevel level, Microsoft.Extensions.Logging.LogLevel targetLevel, string message, object[] args)
    {
        if (Level > level || !_logger.IsEnabled(targetLevel)) return;
        // DiscordRPC uses composite formatting, not structured logging templates.
        _logger.Log(targetLevel, "[DiscordRPC] {Message}", args.Length == 0 ? message : string.Format(message, args));
    }
}
