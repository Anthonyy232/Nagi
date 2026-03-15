using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Data.Common;

namespace Nagi.Core.Data.Interceptors;

/// <summary>
///     Applies SQLite performance PRAGMAs on every new connection.
///     Ensures optimized settings (WAL, cache, temp store) are in effect for all DB operations,
///     not just the initial startup connection.
/// </summary>
public sealed class SqlitePragmaInterceptor : DbConnectionInterceptor
{
    private static readonly SqlitePragmaInterceptor _instance = new();
    public static SqlitePragmaInterceptor Instance => _instance;

    private SqlitePragmaInterceptor() { }

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        ApplyPragmas(connection);
    }

    public override Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        ApplyPragmas(connection);
        return Task.CompletedTask;
    }

    private static void ApplyPragmas(DbConnection connection)
    {
        if (connection is not SqliteConnection) return;
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
