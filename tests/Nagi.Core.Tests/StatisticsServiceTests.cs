using FluentAssertions;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Implementations;
using Nagi.Core.Tests.Utils;
using Xunit;

namespace Nagi.Core.Tests;

public class StatisticsServiceTests : IDisposable
{
    private readonly DbContextFactoryTestHelper _dbHelper;
    private readonly StatisticsService _statisticsService;

    public StatisticsServiceTests()
    {
        _dbHelper = new DbContextFactoryTestHelper();
        _statisticsService = new StatisticsService(_dbHelper.ContextFactory);
    }

    public void Dispose()
    {
        _dbHelper.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task GetTopSongsAsync_CountsFinishedListenAsPlay_WhenNotScrobbleEligible()
    {
        var folder = new Folder { Name = "Test Folder", Path = "C:\\Music" };
        var artist = new Artist { Name = "Artist A" };
        var song = CreateSong(folder, artist, "Song A", null, TimeSpan.FromMinutes(3));

        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Folders.Add(folder);
            context.Artists.Add(artist);
            context.Songs.Add(song);
            context.ListenHistory.Add(new ListenHistory
            {
                Song = song,
                ListenTimestampUtc = DateTime.UtcNow,
                EndReason = PlaybackEndReason.Finished,
                ListenDurationTicks = TimeSpan.FromMinutes(3).Ticks,
                IsEligibleForScrobbling = false
            });
            await context.SaveChangesAsync();
        }

        var result = (await _statisticsService.GetTopSongsAsync(new TimeRange(null, null), 10, SortMetric.PlayCount)).ToList();

        result.Should().ContainSingle();
        result[0].Song.Id.Should().Be(song.Id);
        result[0].TotalPlays.Should().Be(1);
        result[0].TotalDuration.Should().Be(TimeSpan.FromMinutes(3));
    }

    /// <summary>
    ///     Regression test: a song skipped after meeting the scrobble threshold
    ///     (IsEligibleForScrobbling = true, EndReason = Skipped) must count as a play.
    ///     Before the fix, non-Last.fm users always had IsEligibleForScrobbling = false,
    ///     so songs skipped after the threshold incorrectly showed 0 plays.
    /// </summary>
    [Fact]
    public async Task GetTopSongsAsync_CountsSkippedButEligibleListenAsPlay()
    {
        var folder = new Folder { Name = "Test Folder", Path = "C:\\Music" };
        var artist = new Artist { Name = "Artist A" };
        var song = CreateSong(folder, artist, "Song A", null, TimeSpan.FromMinutes(5));

        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Folders.Add(folder);
            context.Artists.Add(artist);
            context.Songs.Add(song);
            context.ListenHistory.Add(new ListenHistory
            {
                Song = song,
                ListenTimestampUtc = DateTime.UtcNow,
                // Skipped at 55% — meets the ≥ 50% threshold, so eligible
                EndReason = PlaybackEndReason.Skipped,
                ListenDurationTicks = TimeSpan.FromMinutes(2.75).Ticks,
                IsEligibleForScrobbling = true
            });
            await context.SaveChangesAsync();
        }

        var result = (await _statisticsService.GetTopSongsAsync(new TimeRange(null, null), 10, SortMetric.PlayCount)).ToList();

        result.Should().ContainSingle();
        result[0].Song.Id.Should().Be(song.Id);
        result[0].TotalPlays.Should().Be(1, "skipping after the threshold should still count as a play");
    }

    [Fact]
    public async Task GetTopAlbumsAsync_CountsFinishedListenAsPlay_WhenNotScrobbleEligible()
    {
        var folder = new Folder { Name = "Test Folder", Path = "C:\\Music" };
        var artist = new Artist { Name = "Artist A" };
        var album = new Album { Title = "Album A", ArtistName = artist.Name, PrimaryArtistName = artist.Name };
        var song = CreateSong(folder, artist, "Song A", album, TimeSpan.FromMinutes(4));

        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Folders.Add(folder);
            context.Artists.Add(artist);
            context.Albums.Add(album);
            context.Songs.Add(song);
            context.ListenHistory.Add(new ListenHistory
            {
                Song = song,
                ListenTimestampUtc = DateTime.UtcNow,
                EndReason = PlaybackEndReason.Finished,
                ListenDurationTicks = TimeSpan.FromMinutes(4).Ticks,
                IsEligibleForScrobbling = false
            });
            await context.SaveChangesAsync();
        }

        var result = (await _statisticsService.GetTopAlbumsAsync(new TimeRange(null, null), 10)).ToList();

        result.Should().ContainSingle();
        result[0].Album.Id.Should().Be(album.Id);
        result[0].TotalPlays.Should().Be(1);
    }

    [Fact]
    public async Task GetUniqueSongsPlayedAsync_CountsFinishedListen_WhenNotScrobbleEligible()
    {
        var folder = new Folder { Name = "Test Folder", Path = "C:\\Music" };
        var artist = new Artist { Name = "Artist A" };
        var playedSong = CreateSong(folder, artist, "Played", null, TimeSpan.FromMinutes(3));
        var skippedSong = CreateSong(folder, artist, "Skipped", null, TimeSpan.FromMinutes(3));

        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Folders.Add(folder);
            context.Artists.Add(artist);
            context.Songs.AddRange(playedSong, skippedSong);
            context.ListenHistory.AddRange(
                new ListenHistory
                {
                    Song = playedSong,
                    ListenTimestampUtc = DateTime.UtcNow,
                    EndReason = PlaybackEndReason.Finished,
                    ListenDurationTicks = TimeSpan.FromMinutes(3).Ticks,
                    IsEligibleForScrobbling = false
                },
                new ListenHistory
                {
                    Song = skippedSong,
                    ListenTimestampUtc = DateTime.UtcNow,
                    EndReason = PlaybackEndReason.Skipped,
                    ListenDurationTicks = TimeSpan.FromSeconds(10).Ticks,
                    IsEligibleForScrobbling = false
                });
            await context.SaveChangesAsync();
        }

        var uniqueSongsPlayed = await _statisticsService.GetUniqueSongsPlayedAsync(new TimeRange(null, null));

        uniqueSongsPlayed.Should().Be(1);
    }

    [Fact]
    public async Task GetTopArtistsAsync_ExcludesArtistsWithOnlyZeroDurationAndZeroPlays()
    {
        var folder = new Folder { Name = "Test Folder", Path = "C:\\Music" };
        var activeArtist = new Artist { Name = "Active Artist" };
        var abandonedArtist = new Artist { Name = "Abandoned Artist" };

        var activeSong = CreateSong(folder, activeArtist, "Active Song", null, TimeSpan.FromMinutes(2));
        var abandonedSong = CreateSong(folder, abandonedArtist, "Abandoned Song", null, TimeSpan.FromMinutes(2));

        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Folders.Add(folder);
            context.Artists.AddRange(activeArtist, abandonedArtist);
            context.Songs.AddRange(activeSong, abandonedSong);
            context.ListenHistory.AddRange(
                new ListenHistory
                {
                    Song = activeSong,
                    ListenTimestampUtc = DateTime.UtcNow,
                    EndReason = PlaybackEndReason.Finished,
                    ListenDurationTicks = TimeSpan.FromMinutes(2).Ticks,
                    IsEligibleForScrobbling = false
                },
                new ListenHistory
                {
                    Song = abandonedSong,
                    ListenTimestampUtc = DateTime.UtcNow,
                    EndReason = PlaybackEndReason.PausedAndAbandoned,
                    ListenDurationTicks = 0,
                    IsEligibleForScrobbling = false
                });
            await context.SaveChangesAsync();
        }

        var topArtists = (await _statisticsService.GetTopArtistsAsync(new TimeRange(null, null), 10, SortMetric.Duration)).ToList();

        topArtists.Select(a => a.Artist.Id).Should().Contain(activeArtist.Id);
        topArtists.Select(a => a.Artist.Id).Should().NotContain(abandonedArtist.Id);
    }

    [Fact]
    public async Task GetTopSongsAsync_FiltersBySearchTerm()
    {
        var folder = new Folder { Name = "Test Folder", Path = "C:\\Music" };
        var artist = new Artist { Name = "Artist A" };
        var song1 = CreateSong(folder, artist, "Apple", null, TimeSpan.FromMinutes(3));
        var song2 = CreateSong(folder, artist, "Banana", null, TimeSpan.FromMinutes(3));

        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Folders.Add(folder);
            context.Artists.Add(artist);
            context.Songs.AddRange(song1, song2);
            context.ListenHistory.AddRange(
                new ListenHistory { Song = song1, ListenTimestampUtc = DateTime.UtcNow, EndReason = PlaybackEndReason.Finished, ListenDurationTicks = TimeSpan.FromMinutes(3).Ticks },
                new ListenHistory { Song = song2, ListenTimestampUtc = DateTime.UtcNow, EndReason = PlaybackEndReason.Finished, ListenDurationTicks = TimeSpan.FromMinutes(3).Ticks }
            );
            await context.SaveChangesAsync();
        }

        var result = (await _statisticsService.GetTopSongsAsync(new TimeRange(null, null), 10, SortMetric.PlayCount, searchTerm: "App")).ToList();

        result.Should().ContainSingle();
        result[0].Song.Title.Should().Be("Apple");
    }

    [Fact]
    public async Task GetTopArtistsAsync_FiltersBySearchTerm()
    {
        var folder = new Folder { Name = "Test Folder", Path = "C:\\Music" };
        var artist1 = new Artist { Name = "Alpha" };
        var artist2 = new Artist { Name = "Beta" };
        var song1 = CreateSong(folder, artist1, "Song 1", null, TimeSpan.FromMinutes(3));
        var song2 = CreateSong(folder, artist2, "Song 2", null, TimeSpan.FromMinutes(3));

        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Folders.Add(folder);
            context.Artists.AddRange(artist1, artist2);
            context.Songs.AddRange(song1, song2);
            context.ListenHistory.AddRange(
                new ListenHistory { Song = song1, ListenTimestampUtc = DateTime.UtcNow, EndReason = PlaybackEndReason.Finished, ListenDurationTicks = TimeSpan.FromMinutes(3).Ticks },
                new ListenHistory { Song = song2, ListenTimestampUtc = DateTime.UtcNow, EndReason = PlaybackEndReason.Finished, ListenDurationTicks = TimeSpan.FromMinutes(3).Ticks }
            );
            await context.SaveChangesAsync();
        }

        var result = (await _statisticsService.GetTopArtistsAsync(new TimeRange(null, null), 10, SortMetric.Duration, searchTerm: "Alp")).ToList();

        result.Should().ContainSingle();
        result[0].Artist.Name.Should().Be("Alpha");
    }

    [Fact]
    public async Task GetTopAlbumsAsync_FiltersBySearchTerm()
    {
        var folder = new Folder { Name = "Test Folder", Path = "C:\\Music" };
        var artist = new Artist { Name = "Artist A" };
        var album1 = new Album { Title = "First Album", ArtistName = artist.Name, PrimaryArtistName = artist.Name };
        var album2 = new Album { Title = "Second Album", ArtistName = artist.Name, PrimaryArtistName = artist.Name };
        var song1 = CreateSong(folder, artist, "Song 1", album1, TimeSpan.FromMinutes(3));
        var song2 = CreateSong(folder, artist, "Song 2", album2, TimeSpan.FromMinutes(3));

        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Folders.Add(folder);
            context.Artists.Add(artist);
            context.Albums.AddRange(album1, album2);
            context.Songs.AddRange(song1, song2);
            context.ListenHistory.AddRange(
                new ListenHistory { Song = song1, ListenTimestampUtc = DateTime.UtcNow, EndReason = PlaybackEndReason.Finished, ListenDurationTicks = TimeSpan.FromMinutes(3).Ticks },
                new ListenHistory { Song = song2, ListenTimestampUtc = DateTime.UtcNow, EndReason = PlaybackEndReason.Finished, ListenDurationTicks = TimeSpan.FromMinutes(3).Ticks }
            );
            await context.SaveChangesAsync();
        }

        var result = (await _statisticsService.GetTopAlbumsAsync(new TimeRange(null, null), 10, searchTerm: "First")).ToList();

        result.Should().ContainSingle();
        result[0].Album.Title.Should().Be("First Album");
    }

    [Fact]
    public async Task GetTopGenresAsync_FiltersBySearchTerm()
    {
        var folder = new Folder { Name = "Test Folder", Path = "C:\\Music" };
        var artist = new Artist { Name = "Artist A" };
        var song1 = CreateSong(folder, artist, "Song 1", null, TimeSpan.FromMinutes(3));
        var song2 = CreateSong(folder, artist, "Song 2", null, TimeSpan.FromMinutes(3));

        var genre1 = new Genre { Name = "Rock" };
        var genre2 = new Genre { Name = "Pop" };

        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Folders.Add(folder);
            context.Artists.Add(artist);
            context.Genres.AddRange(genre1, genre2);
            context.Songs.AddRange(song1, song2);

            song1.Genres.Add(genre1);
            song2.Genres.Add(genre2);

            context.ListenHistory.AddRange(
                new ListenHistory { Song = song1, ListenTimestampUtc = DateTime.UtcNow, EndReason = PlaybackEndReason.Finished, ListenDurationTicks = TimeSpan.FromMinutes(3).Ticks },
                new ListenHistory { Song = song2, ListenTimestampUtc = DateTime.UtcNow, EndReason = PlaybackEndReason.Finished, ListenDurationTicks = TimeSpan.FromMinutes(3).Ticks }
            );
            await context.SaveChangesAsync();
        }

        var result = (await _statisticsService.GetTopGenresAsync(new TimeRange(null, null), 10, searchTerm: "Ro")).ToList();

        result.Should().ContainSingle();
        result[0].Genre.Name.Should().Be("Rock");
    }

    private static Song CreateSong(Folder folder, Artist artist, string title, Album? album, TimeSpan duration)
    {
        var song = new Song
        {
            Title = title,
            Folder = folder,
            Album = album,
            FilePath = $"C:\\Music\\{Guid.NewGuid()}.mp3",
            DirectoryPath = "C:\\Music",
            DurationTicks = duration.Ticks
        };

        song.SongArtists.Add(new SongArtist
        {
            Song = song,
            Artist = artist,
            Order = 0
        });

        return song;
    }
}
