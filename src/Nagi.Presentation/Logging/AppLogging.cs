using Serilog;
using Serilog.Events;

namespace Nagi.Presentation.Logging;

/// <summary>
///     Sets up the app's logging. Recent log lines are kept in memory so they can be attached to crash reports.
/// </summary>
public static class AppLogging
{
    /// <summary>
    ///     Configures the global Serilog logger. Returns the path of the log file.
    /// </summary>
    public static string Configure(string logsDirectory)
    {
        var logFilePath = Path.Combine(logsDirectory, "log.txt");

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)
            .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.EntityFrameworkCore.Migrations", LogEventLevel.Error)
            .MinimumLevel.Override("Microsoft.EntityFrameworkCore.Database.Command", LogEventLevel.Error)
            .Enrich.FromLogContext()
            .Enrich.WithThreadId()
            .WriteTo.Debug()
            .WriteTo.File(logFilePath,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                fileSizeLimitBytes: 10 * 1024 * 1024,
                rollOnFileSizeLimit: true,
                outputTemplate:
                "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] [{ThreadId}] {Message:lj}{NewLine}{Exception}")
            .WriteTo.Sink(MemoryLog.Instance)
            .CreateLogger();

        return logFilePath;
    }

    public static string BuildCrashReport(Exception exception) =>
        $"{MemoryLog.Instance.GetContent()}\n\n--- EXCEPTION DETAILS ---\n{exception}";
}
