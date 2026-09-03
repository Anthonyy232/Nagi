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
                .UseSqlite($"Data Source={_databasePath};Pooling=False")
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

        // Ensure the database schema is created
        using (var context = new MusicDbContext(options))
        {
            context.Database.EnsureCreated();
        }

        ContextFactory = new TestDbContextFactory(options);
    }

    /// <summary>
    ///     Gets the configured <see cref="IDbContextFactory{MusicDbContext}" /> for the in-memory database.
    /// </summary>
    public IDbContextFactory<MusicDbContext> ContextFactory { get; }

    /// <summary>
    ///     Disposes the underlying database connection, effectively deleting the in-memory database.
    /// </summary>
    public void Dispose()
    {
        _connection?.Dispose();
        if (_databasePath is not null)
        {
            File.Delete(_databasePath);
            File.Delete($"{_databasePath}-wal");
            File.Delete($"{_databasePath}-shm");
        }
        GC.SuppressFinalize(this);
    }

    /// <summary>
    ///     A simple implementation of IDbContextFactory for testing.
    /// </summary>
    private class TestDbContextFactory : IDbContextFactory<MusicDbContext>
    {
        private readonly DbContextOptions<MusicDbContext> _options;

        public TestDbContextFactory(DbContextOptions<MusicDbContext> options)
        {
            _options = options;
        }

        public MusicDbContext CreateDbContext()
        {
            return new MusicDbContext(_options);
        }
    }
}
