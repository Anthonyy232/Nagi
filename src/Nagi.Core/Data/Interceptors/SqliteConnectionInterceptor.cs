using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Data.Common;
using System.Globalization;

namespace Nagi.Core.Data.Interceptors;

/// <summary>
///     Configures SQLite connection settings, native .NET conversions used by migrations, and Unicode case folding.
/// </summary>
public sealed class SqliteConnectionInterceptor : DbConnectionInterceptor
{
    public static SqliteConnectionInterceptor Instance { get; } = new();

    private SqliteConnectionInterceptor() { }

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        ConfigureConnection(connection);
    }

    public override Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        ConfigureConnection(connection);
        return Task.CompletedTask;
    }

    private static void ConfigureConnection(DbConnection connection)
    {
        if (connection is not SqliteConnection sqlite) return;
        sqlite.CreateFunction("nagi_duration_ticks", (string value) => TimeSpan.Parse(value, CultureInfo.InvariantCulture).Ticks,
            isDeterministic: true);
        sqlite.CreateFunction("nagi_duration_text", (long ticks) => TimeSpan.FromTicks(ticks).ToString("c", CultureInfo.InvariantCulture),
            isDeterministic: true);
        sqlite.CreateFunction("nagi_directory_path", (string path) => Path.GetDirectoryName(path) ?? string.Empty,
            isDeterministic: true);
        // SQLite's built-in lower() folds only ASCII, so smart playlist rules would miss accented capitals.
        sqlite.CreateFunction("lower", (string? value) => value?.ToLowerInvariant(), isDeterministic: true);
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "PRAGMA journal_mode=WAL; " +
            "PRAGMA synchronous=NORMAL; " +
            "PRAGMA cache_size=-32000; " +
            "PRAGMA temp_store=MEMORY; " +
            "PRAGMA busy_timeout=5000;";
        cmd.ExecuteNonQuery();
    }
}
