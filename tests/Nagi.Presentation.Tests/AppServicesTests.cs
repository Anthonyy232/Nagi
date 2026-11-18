using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nagi.Core.Data;
using Xunit;

namespace Nagi.Presentation.Tests;

public class AppServicesTests
{
    [Fact]
    public async Task StartDatabase_MigratesANewDatabase()
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = $"nagi-tests-{Guid.NewGuid():N}",
            Mode = SqliteOpenMode.Memory,
            Cache = SqliteCacheMode.Shared
        }.ToString();
        // Keeps the in-memory database alive between contexts.
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var services = new ServiceCollection()
            .AddDbContextFactory<MusicDbContext>(options => options.UseSqlite(connectionString))
            .BuildServiceProvider();

        await AppServices.StartDatabase(services);

        await using var context = await services.GetRequiredService<IDbContextFactory<MusicDbContext>>()
            .CreateDbContextAsync();
        (await context.Database.GetPendingMigrationsAsync()).Should().BeEmpty();
        (await context.Songs.CountAsync()).Should().Be(0);
    }
}
