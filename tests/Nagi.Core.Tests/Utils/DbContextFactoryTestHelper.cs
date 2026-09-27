using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Nagi.Core.Data;

namespace Nagi.Core.Tests.Utils;

/// <summary>
///     Creates an isolated SQLite database for each test.
/// </summary>
public class DbContextFactoryTestHelper : IDisposable
{
    private readonly SqliteConnection? _connection;
    private readonly string? _databasePath;

    /// <summary>
    ///     Creates an in-memory database by default, or a file database for concurrency tests.
    /// </summary>
    public DbContextFactoryTestHelper(bool useFileDatabase = false)
    {
        DbContextOptions<MusicDbContext> options;
        if (useFileDatabase)
        {
            _databasePath = Path.Combine(Path.GetTempPath(), $"nagi-tests-{Guid.NewGuid():N}.db");
            options = new DbContextOptionsBuilder<MusicDbContext>()
                .UseSqlite(new SqliteConnectionStringBuilder { DataSource = _databasePath, Pooling = false }.ToString())
                .Options;
        }
        else
        {
            _connection = new SqliteConnection("DataSource=:memory:");
            _connection.Open();
            options = new DbContextOptionsBuilder<MusicDbContext>()
                .UseSqlite(_connection)
                .Options;
        }

        using (var context = new MusicDbContext(options))
        {
            context.Database.EnsureCreated();
        }

        ContextFactory = new TestDbContextFactory(options);
    }

    public IDbContextFactory<MusicDbContext> ContextFactory { get; }

    public void Dispose()
    {
        _connection?.Dispose();
        if (_databasePath is not null)
        {
            File.Delete(_databasePath);
            File.Delete($"{_databasePath}-wal");
            File.Delete($"{_databasePath}-shm");
        }
    }

    private sealed class TestDbContextFactory(DbContextOptions<MusicDbContext> options) : IDbContextFactory<MusicDbContext>
    {
        public MusicDbContext CreateDbContext() => new(options);
    }
}
