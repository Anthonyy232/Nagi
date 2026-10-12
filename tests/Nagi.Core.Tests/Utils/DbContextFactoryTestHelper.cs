using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Nagi.Core.Data;
using Nagi.Core.Models;

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

    /// <summary>
    ///     Seeds one playlist whose songs carry the given <see cref="PlaylistSong.Order" /> values, in order.
    /// </summary>
    public async Task<(Playlist Playlist, Song[] Songs)> SeedPlaylistAsync(params double[] orders)
    {
        var folder = new Folder { Name = "Music", Path = @"C:\Music" };
        var songs = orders.Select((_, i) => new Song
        {
            Title = $"Song {i}", FilePath = $@"C:\Music\{i}.mp3", DirectoryPath = folder.Path,
            Folder = folder, TrackNumber = orders.Length - i, Lyrics = "Heavy lyrics", Grouping = "Group"
        }).ToArray();
        var playlist = new Playlist { Name = "Test", DateModified = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc) };
        await using var context = ContextFactory.CreateDbContext();
        context.PlaylistSongs.AddRange(songs.Select((song, i) => new PlaylistSong { Playlist = playlist, Song = song, Order = orders[i] }));
        await context.SaveChangesAsync();
        return (playlist, songs);
    }

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
