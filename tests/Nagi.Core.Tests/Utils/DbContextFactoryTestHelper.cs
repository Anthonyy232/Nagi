using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Nagi.Core.Data;

namespace Nagi.Core.Tests.Utils;

/// <summary>
///     A helper class for creating an in-memory SQLite database context factory for testing purposes.
///     Each instance creates a unique, isolated database connection that is disposed of with the helper.
/// </summary>
public class DbContextFactoryTestHelper : IDisposable
{
    private readonly SqliteConnection _connection;

    /// <summary>
    ///     Initializes a new instance of the <see cref="DbContextFactoryTestHelper" /> class,
    ///     creating a new in-memory SQLite database.
    /// </summary>
    public DbContextFactoryTestHelper()
    {
        // Use "DataSource=:memory:" for a private in-memory database that is deleted when the connection is closed.
        // "Mode=Memory" and "Cache=Shared" can be used for a shared in-memory database if needed across multiple connections.
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<MusicDbContext>()
            .UseSqlite(_connection)
            .Options;

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
        _connection.Close();
        _connection.Dispose();
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