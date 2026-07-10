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
    public async Task GetTopAlbumsAsync_AlbumlessListensDoNotConsumePageSlots()
    {
        var folder = new Folder { Name = "Test Folder", Path = "C:\\Music" };
        var artist = new Artist { Name = "Artist A" };
        var firstAlbum = new Album { Title = "Album A", ArtistName = artist.Name, PrimaryArtistName = artist.Name };
        var secondAlbum = new Album { Title = "Album B", ArtistName = artist.Name, PrimaryArtistName = artist.Name };
        var firstSong = CreateSong(folder, artist, "Song A", firstAlbum, TimeSpan.FromMinutes(3));
        var secondSong = CreateSong(folder, artist, "Song B", secondAlbum, TimeSpan.FromMinutes(3));
        var albumlessSong = CreateSong(folder, artist, "Loose Track", null, TimeSpan.FromMinutes(3));

        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Folders.Add(folder);
            context.Artists.Add(artist);
            context.Albums.AddRange(firstAlbum, secondAlbum);
            context.Songs.AddRange(firstSong, secondSong, albumlessSong);
            context.ListenHistory.AddRange(
                new ListenHistory
                {
                    Song = firstSong,
                    ListenTimestampUtc = DateTime.UtcNow,
                    EndReason = PlaybackEndReason.Finished,
                    ListenDurationTicks = firstSong.DurationTicks
                },
                new ListenHistory
                {
                    Song = secondSong,
                    ListenTimestampUtc = DateTime.UtcNow,
                    EndReason = PlaybackEndReason.Finished,
                    ListenDurationTicks = secondSong.DurationTicks
                });
            for (var i = 0; i < 10; i++)
            {
                context.ListenHistory.Add(new ListenHistory
                {
                    Song = albumlessSong,
                    ListenTimestampUtc = DateTime.UtcNow.AddSeconds(i),
                    EndReason = PlaybackEndReason.Finished,
                    ListenDurationTicks = albumlessSong.DurationTicks
                });
            }
            await context.SaveChangesAsync();
        }

        var result = (await _statisticsService.GetTopAlbumsAsync(
            new TimeRange(null, null), limit: 2, metric: SortMetric.PlayCount)).ToList();
        var count = await _statisticsService.GetTopAlbumsCountAsync(new TimeRange(null, null));

        result.Select(item => item.Album.Id).Should().BeEquivalentTo(new[] { firstAlbum.Id, secondAlbum.Id });
        result.Should().HaveCount(count);
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

    // -------------------------------------------------------------------------
    // TimeRange date filtering
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GetTopSongsAsync_WithStartDateFilter_ExcludesListensBeforeStart()
    {
        var folder = new Folder { Name = "F", Path = "C:\\Music" };
        var artist = new Artist { Name = "A" };
        var oldSong = CreateSong(folder, artist, "Old Song", null, TimeSpan.FromMinutes(3));
        var newSong = CreateSong(folder, artist, "New Song", null, TimeSpan.FromMinutes(3));
        var boundary = new DateTime(2024, 1, 15, 0, 0, 0, DateTimeKind.Utc);

        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Folders.Add(folder);
            context.Artists.Add(artist);
            context.Songs.AddRange(oldSong, newSong);
            context.ListenHistory.AddRange(
                new ListenHistory { Song = oldSong, ListenTimestampUtc = boundary.AddDays(-1), EndReason = PlaybackEndReason.Finished, ListenDurationTicks = TimeSpan.FromMinutes(3).Ticks },
                new ListenHistory { Song = newSong, ListenTimestampUtc = boundary.AddDays(1), EndReason = PlaybackEndReason.Finished, ListenDurationTicks = TimeSpan.FromMinutes(3).Ticks }
            );
            await context.SaveChangesAsync();
        }

        var result = (await _statisticsService.GetTopSongsAsync(new TimeRange(boundary, null), 10, SortMetric.PlayCount)).ToList();

        result.Should().ContainSingle("only the listen after the start date should be included");
        result[0].Song.Id.Should().Be(newSong.Id);
    }

    [Fact]
    public async Task GetTopSongsAsync_WithEndDateFilter_ExcludesListensAfterEnd()
    {
        var folder = new Folder { Name = "F", Path = "C:\\Music" };
        var artist = new Artist { Name = "A" };
        var oldSong = CreateSong(folder, artist, "Old Song", null, TimeSpan.FromMinutes(3));
        var newSong = CreateSong(folder, artist, "New Song", null, TimeSpan.FromMinutes(3));
        var boundary = new DateTime(2024, 1, 15, 0, 0, 0, DateTimeKind.Utc);

        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Folders.Add(folder);
            context.Artists.Add(artist);
            context.Songs.AddRange(oldSong, newSong);
            context.ListenHistory.AddRange(
                new ListenHistory { Song = oldSong, ListenTimestampUtc = boundary.AddDays(-1), EndReason = PlaybackEndReason.Finished, ListenDurationTicks = TimeSpan.FromMinutes(3).Ticks },
                new ListenHistory { Song = newSong, ListenTimestampUtc = boundary.AddDays(1), EndReason = PlaybackEndReason.Finished, ListenDurationTicks = TimeSpan.FromMinutes(3).Ticks }
            );
            await context.SaveChangesAsync();
        }

        var result = (await _statisticsService.GetTopSongsAsync(new TimeRange(null, boundary), 10, SortMetric.PlayCount)).ToList();

        result.Should().ContainSingle("only the listen before the end date should be included");
        result[0].Song.Id.Should().Be(oldSong.Id);
    }

    [Fact]
    public async Task GetTopArtistsAsync_WithTimeRange_ExcludesOutOfRangeListens()
    {
        var folder = new Folder { Name = "F", Path = "C:\\Music" };
        var artistA = new Artist { Name = "Artist A" };
        var artistB = new Artist { Name = "Artist B" };
        var songA = CreateSong(folder, artistA, "Song A", null, TimeSpan.FromMinutes(3));
        var songB = CreateSong(folder, artistB, "Song B", null, TimeSpan.FromMinutes(3));
        var start = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc);

        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Folders.Add(folder);
            context.Artists.AddRange(artistA, artistB);
            context.Songs.AddRange(songA, songB);
            context.ListenHistory.AddRange(
                // artistA's listen is before the range
                new ListenHistory { Song = songA, ListenTimestampUtc = start.AddDays(-5), EndReason = PlaybackEndReason.Finished, ListenDurationTicks = TimeSpan.FromMinutes(3).Ticks },
                // artistB's listen is within the range
                new ListenHistory { Song = songB, ListenTimestampUtc = start.AddDays(1), EndReason = PlaybackEndReason.Finished, ListenDurationTicks = TimeSpan.FromMinutes(3).Ticks }
            );
            await context.SaveChangesAsync();
        }

        var result = (await _statisticsService.GetTopArtistsAsync(new TimeRange(start, null), 10, SortMetric.PlayCount)).ToList();

        result.Should().ContainSingle();
        result[0].Artist.Id.Should().Be(artistB.Id);
    }

    // -------------------------------------------------------------------------
    // Sort metric
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GetTopSongsAsync_WithDurationSortMetric_OrdersByTotalDurationDescending()
    {
        var folder = new Folder { Name = "F", Path = "C:\\Music" };
        var artist = new Artist { Name = "A" };
        // highPlaysSong: 5 short listens → more plays, less total time
        var highPlaysSong = CreateSong(folder, artist, "High Plays", null, TimeSpan.FromMinutes(1));
        // longDurationSong: 1 long listen → fewer plays, more total time
        var longDurationSong = CreateSong(folder, artist, "Long Duration", null, TimeSpan.FromHours(1));

        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Folders.Add(folder);
            context.Artists.Add(artist);
            context.Songs.AddRange(highPlaysSong, longDurationSong);
            // 5 listens × 1 min = 5 min total
            for (var i = 0; i < 5; i++)
                context.ListenHistory.Add(new ListenHistory { Song = highPlaysSong, ListenTimestampUtc = DateTime.UtcNow, EndReason = PlaybackEndReason.Finished, ListenDurationTicks = TimeSpan.FromMinutes(1).Ticks });
            // 1 listen × 60 min = 60 min total
            context.ListenHistory.Add(new ListenHistory { Song = longDurationSong, ListenTimestampUtc = DateTime.UtcNow, EndReason = PlaybackEndReason.Finished, ListenDurationTicks = TimeSpan.FromHours(1).Ticks });
            await context.SaveChangesAsync();
        }

        var byPlayCount = (await _statisticsService.GetTopSongsAsync(new TimeRange(null, null), 10, SortMetric.PlayCount)).ToList();
        var byDuration = (await _statisticsService.GetTopSongsAsync(new TimeRange(null, null), 10, SortMetric.Duration)).ToList();

        byPlayCount[0].Song.Id.Should().Be(highPlaysSong.Id, "5 plays should rank first when sorting by play count");
        byDuration[0].Song.Id.Should().Be(longDurationSong.Id, "60 min total should rank first when sorting by duration");
    }

    // -------------------------------------------------------------------------
    // Skip tracking
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GetTopSongsAsync_SongWithOnlyPureSkips_DoesNotAppearInResults()
    {
        // A song that was only skipped early (not scrobble-eligible) should have
        // TotalPlays = 0 and therefore must not appear in top songs results.
        var folder = new Folder { Name = "F", Path = "C:\\Music" };
        var artist = new Artist { Name = "A" };
        var skippedSong = CreateSong(folder, artist, "Skipped", null, TimeSpan.FromMinutes(3));

        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Folders.Add(folder);
            context.Artists.Add(artist);
            context.Songs.Add(skippedSong);
            context.ListenHistory.Add(new ListenHistory
            {
                Song = skippedSong,
                ListenTimestampUtc = DateTime.UtcNow,
                EndReason = PlaybackEndReason.Skipped,
                ListenDurationTicks = TimeSpan.FromSeconds(10).Ticks,
                IsEligibleForScrobbling = false // skipped before the 50% threshold
            });
            await context.SaveChangesAsync();
        }

        var result = (await _statisticsService.GetTopSongsAsync(new TimeRange(null, null), 10, SortMetric.PlayCount)).ToList();

        result.Should().BeEmpty("a song that was only skipped before the threshold should have 0 plays and be excluded");
    }

    // -------------------------------------------------------------------------
    // Count methods (drive pagination UI)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GetTopSongsCountAsync_ReturnsNumberOfSongsWithAtLeastOnePlay()
    {
        var folder = new Folder { Name = "F", Path = "C:\\Music" };
        var artist = new Artist { Name = "A" };
        var playedSong1 = CreateSong(folder, artist, "Played 1", null, TimeSpan.FromMinutes(3));
        var playedSong2 = CreateSong(folder, artist, "Played 2", null, TimeSpan.FromMinutes(3));
        var skippedSong = CreateSong(folder, artist, "Skipped Only", null, TimeSpan.FromMinutes(3));

        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Folders.Add(folder);
            context.Artists.Add(artist);
            context.Songs.AddRange(playedSong1, playedSong2, skippedSong);
            context.ListenHistory.AddRange(
                new ListenHistory { Song = playedSong1, ListenTimestampUtc = DateTime.UtcNow, EndReason = PlaybackEndReason.Finished, ListenDurationTicks = TimeSpan.FromMinutes(3).Ticks },
                new ListenHistory { Song = playedSong2, ListenTimestampUtc = DateTime.UtcNow, EndReason = PlaybackEndReason.Finished, ListenDurationTicks = TimeSpan.FromMinutes(3).Ticks },
                new ListenHistory { Song = skippedSong, ListenTimestampUtc = DateTime.UtcNow, EndReason = PlaybackEndReason.Skipped, ListenDurationTicks = TimeSpan.FromSeconds(5).Ticks, IsEligibleForScrobbling = false }
            );
            await context.SaveChangesAsync();
        }

        var count = await _statisticsService.GetTopSongsCountAsync(new TimeRange(null, null));

        count.Should().Be(2, "only the 2 songs with at least one qualifying play should be counted");
    }

    [Fact]
    public async Task GetTopSongsCountAsync_WithSearchTerm_ReturnsMatchingCount()
    {
        var folder = new Folder { Name = "F", Path = "C:\\Music" };
        var artist = new Artist { Name = "A" };
        var rockSong = CreateSong(folder, artist, "Rock Anthem", null, TimeSpan.FromMinutes(3));
        var popSong = CreateSong(folder, artist, "Pop Hit", null, TimeSpan.FromMinutes(3));

        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Folders.Add(folder);
            context.Artists.Add(artist);
            context.Songs.AddRange(rockSong, popSong);
            context.ListenHistory.AddRange(
                new ListenHistory { Song = rockSong, ListenTimestampUtc = DateTime.UtcNow, EndReason = PlaybackEndReason.Finished, ListenDurationTicks = TimeSpan.FromMinutes(3).Ticks },
                new ListenHistory { Song = popSong, ListenTimestampUtc = DateTime.UtcNow, EndReason = PlaybackEndReason.Finished, ListenDurationTicks = TimeSpan.FromMinutes(3).Ticks }
            );
            await context.SaveChangesAsync();
        }

        var count = await _statisticsService.GetTopSongsCountAsync(new TimeRange(null, null), searchTerm: "Rock");

        count.Should().Be(1, "search filter should narrow the count to matching songs");
    }

    [Fact]
    public async Task GetTopArtistsCountAsync_ReturnsCorrectCount()
    {
        var folder = new Folder { Name = "F", Path = "C:\\Music" };
        var artist1 = new Artist { Name = "Artist 1" };
        var artist2 = new Artist { Name = "Artist 2" };
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

        var count = await _statisticsService.GetTopArtistsCountAsync(new TimeRange(null, null));

        count.Should().Be(2);
    }

    [Fact]
    public async Task GetTopAlbumsCountAsync_ReturnsCorrectCount()
    {
        var folder = new Folder { Name = "F", Path = "C:\\Music" };
        var artist = new Artist { Name = "A" };
        var album1 = new Album { Title = "Album 1", ArtistName = "A", PrimaryArtistName = "A" };
        var album2 = new Album { Title = "Album 2", ArtistName = "A", PrimaryArtistName = "A" };
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

        var count = await _statisticsService.GetTopAlbumsCountAsync(new TimeRange(null, null));

        count.Should().Be(2);
    }

    [Fact]
    public async Task GetTopGenresCountAsync_ReturnsCorrectCount()
    {
        var folder = new Folder { Name = "F", Path = "C:\\Music" };
        var artist = new Artist { Name = "A" };
        var song1 = CreateSong(folder, artist, "Song 1", null, TimeSpan.FromMinutes(3));
        var song2 = CreateSong(folder, artist, "Song 2", null, TimeSpan.FromMinutes(3));
        var genre1 = new Genre { Name = "Rock" };
        var genre2 = new Genre { Name = "Jazz" };

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

        var count = await _statisticsService.GetTopGenresCountAsync(new TimeRange(null, null));

        count.Should().Be(2);
    }

    // -------------------------------------------------------------------------
    // Duration sort metric — Albums & Genres
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GetTopAlbumsAsync_WithDurationSortMetric_OrdersByTotalDurationDescending()
    {
        var folder = new Folder { Name = "F", Path = "C:\\Music" };
        var artist = new Artist { Name = "A" };
        var highPlaysAlbum = new Album { Title = "High Plays Album", ArtistName = "A", PrimaryArtistName = "A" };
        var longDurationAlbum = new Album { Title = "Long Duration Album", ArtistName = "A", PrimaryArtistName = "A" };
        var song1 = CreateSong(folder, artist, "Song 1", highPlaysAlbum, TimeSpan.FromMinutes(1));
        var song2 = CreateSong(folder, artist, "Song 2", longDurationAlbum, TimeSpan.FromHours(1));

        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Folders.Add(folder);
            context.Artists.Add(artist);
            context.Albums.AddRange(highPlaysAlbum, longDurationAlbum);
            context.Songs.AddRange(song1, song2);
            // 5 listens × 1 min = 5 min total for highPlaysAlbum
            for (var i = 0; i < 5; i++)
                context.ListenHistory.Add(new ListenHistory { Song = song1, ListenTimestampUtc = DateTime.UtcNow, EndReason = PlaybackEndReason.Finished, ListenDurationTicks = TimeSpan.FromMinutes(1).Ticks });
            // 1 listen × 60 min = 60 min total for longDurationAlbum
            context.ListenHistory.Add(new ListenHistory { Song = song2, ListenTimestampUtc = DateTime.UtcNow, EndReason = PlaybackEndReason.Finished, ListenDurationTicks = TimeSpan.FromHours(1).Ticks });
            await context.SaveChangesAsync();
        }

        var byPlayCount = (await _statisticsService.GetTopAlbumsAsync(new TimeRange(null, null), 10, SortMetric.PlayCount)).ToList();
        var byDuration = (await _statisticsService.GetTopAlbumsAsync(new TimeRange(null, null), 10, SortMetric.Duration)).ToList();

        byPlayCount[0].Album.Id.Should().Be(highPlaysAlbum.Id, "5 plays should rank first when sorting by play count");
        byDuration[0].Album.Id.Should().Be(longDurationAlbum.Id, "60 min total should rank first when sorting by duration");
    }

    [Fact]
    public async Task GetTopGenresAsync_WithDurationSortMetric_OrdersByTotalDurationDescending()
    {
        var folder = new Folder { Name = "F", Path = "C:\\Music" };
        var artist = new Artist { Name = "A" };
        var highPlaysGenre = new Genre { Name = "Pop" };
        var longDurationGenre = new Genre { Name = "Classical" };
        var song1 = CreateSong(folder, artist, "Song 1", null, TimeSpan.FromMinutes(1));
        var song2 = CreateSong(folder, artist, "Song 2", null, TimeSpan.FromHours(1));

        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Folders.Add(folder);
            context.Artists.Add(artist);
            context.Genres.AddRange(highPlaysGenre, longDurationGenre);
            context.Songs.AddRange(song1, song2);
            song1.Genres.Add(highPlaysGenre);
            song2.Genres.Add(longDurationGenre);
            // 5 listens × 1 min = 5 min total for Pop
            for (var i = 0; i < 5; i++)
                context.ListenHistory.Add(new ListenHistory { Song = song1, ListenTimestampUtc = DateTime.UtcNow, EndReason = PlaybackEndReason.Finished, ListenDurationTicks = TimeSpan.FromMinutes(1).Ticks });
            // 1 listen × 60 min = 60 min total for Classical
            context.ListenHistory.Add(new ListenHistory { Song = song2, ListenTimestampUtc = DateTime.UtcNow, EndReason = PlaybackEndReason.Finished, ListenDurationTicks = TimeSpan.FromHours(1).Ticks });
            await context.SaveChangesAsync();
        }

        var byPlayCount = (await _statisticsService.GetTopGenresAsync(new TimeRange(null, null), 10, SortMetric.PlayCount)).ToList();
        var byDuration = (await _statisticsService.GetTopGenresAsync(new TimeRange(null, null), 10, SortMetric.Duration)).ToList();

        byPlayCount[0].Genre.Id.Should().Be(highPlaysGenre.Id, "5 plays should rank first when sorting by play count");
        byDuration[0].Genre.Id.Should().Be(longDurationGenre.Id, "60 min total should rank first when sorting by duration");
    }

    // -------------------------------------------------------------------------
    // Total listen time
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GetTotalListenTimeAsync_SumsAllListenDurations()
    {
        var folder = new Folder { Name = "F", Path = "C:\\Music" };
        var artist = new Artist { Name = "A" };
        var song = CreateSong(folder, artist, "Song", null, TimeSpan.FromMinutes(5));

        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Folders.Add(folder);
            context.Artists.Add(artist);
            context.Songs.Add(song);
            context.ListenHistory.AddRange(
                new ListenHistory { Song = song, ListenTimestampUtc = DateTime.UtcNow, EndReason = PlaybackEndReason.Finished, ListenDurationTicks = TimeSpan.FromMinutes(2).Ticks },
                new ListenHistory { Song = song, ListenTimestampUtc = DateTime.UtcNow, EndReason = PlaybackEndReason.Skipped, ListenDurationTicks = TimeSpan.FromMinutes(3).Ticks },
                new ListenHistory { Song = song, ListenTimestampUtc = DateTime.UtcNow, EndReason = PlaybackEndReason.Finished, ListenDurationTicks = TimeSpan.FromMinutes(5).Ticks }
            );
            await context.SaveChangesAsync();
        }

        var total = await _statisticsService.GetTotalListenTimeAsync(new TimeRange(null, null));

        total.Should().Be(TimeSpan.FromMinutes(10), "all listen durations should be summed regardless of end reason");
    }

    [Fact]
    public async Task GetTotalListenTimeAsync_WithTimeRange_OnlySumsListensWithinRange()
    {
        var folder = new Folder { Name = "F", Path = "C:\\Music" };
        var artist = new Artist { Name = "A" };
        var song = CreateSong(folder, artist, "Song", null, TimeSpan.FromMinutes(5));
        var start = new DateTime(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc);

        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Folders.Add(folder);
            context.Artists.Add(artist);
            context.Songs.Add(song);
            context.ListenHistory.AddRange(
                new ListenHistory { Song = song, ListenTimestampUtc = start.AddDays(-1), EndReason = PlaybackEndReason.Finished, ListenDurationTicks = TimeSpan.FromHours(1).Ticks },
                new ListenHistory { Song = song, ListenTimestampUtc = start.AddDays(1), EndReason = PlaybackEndReason.Finished, ListenDurationTicks = TimeSpan.FromMinutes(5).Ticks }
            );
            await context.SaveChangesAsync();
        }

        var total = await _statisticsService.GetTotalListenTimeAsync(new TimeRange(start, null));

        total.Should().Be(TimeSpan.FromMinutes(5), "only the listen within the range should contribute to total time");
    }

    // -------------------------------------------------------------------------
    // Most active day / peak listening hour
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GetMostActiveDayOfWeekAsync_WithNoHistory_ReturnsMonday()
    {
        var result = await _statisticsService.GetMostActiveDayOfWeekAsync(new TimeRange(null, null));

        result.Should().Be(DayOfWeek.Monday, "Monday is the documented default when there is no listen history");
    }

    [Fact]
    public async Task GetMostActiveDayOfWeekAsync_ReturnsTheDayWithTheMostListens()
    {
        var folder = new Folder { Name = "F", Path = "C:\\Music" };
        var artist = new Artist { Name = "A" };
        var song = CreateSong(folder, artist, "Song", null, TimeSpan.FromMinutes(3));
        // Use a fixed UTC time so that the expected local day is deterministic.
        var majorityTime = new DateTime(2024, 6, 18, 12, 0, 0, DateTimeKind.Utc); // midday UTC
        var minorityTime = new DateTime(2024, 6, 21, 12, 0, 0, DateTimeKind.Utc); // 3 days later
        var expectedDay = majorityTime.ToLocalTime().DayOfWeek;

        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Folders.Add(folder);
            context.Artists.Add(artist);
            context.Songs.Add(song);
            for (var i = 0; i < 3; i++)
                context.ListenHistory.Add(new ListenHistory { Song = song, ListenTimestampUtc = majorityTime, EndReason = PlaybackEndReason.Finished, ListenDurationTicks = TimeSpan.FromMinutes(3).Ticks });
            context.ListenHistory.Add(new ListenHistory { Song = song, ListenTimestampUtc = minorityTime, EndReason = PlaybackEndReason.Finished, ListenDurationTicks = TimeSpan.FromMinutes(3).Ticks });
            await context.SaveChangesAsync();
        }

        var result = await _statisticsService.GetMostActiveDayOfWeekAsync(new TimeRange(null, null));

        result.Should().Be(expectedDay, "the day with 3 listens should win over the day with 1 listen");
    }

    [Fact]
    public async Task GetPeakListeningHourAsync_WithNoHistory_Returns12()
    {
        var result = await _statisticsService.GetPeakListeningHourAsync(new TimeRange(null, null));

        result.Should().Be(12, "12 is the documented default when there is no listen history");
    }

    [Fact]
    public async Task GetPeakListeningHourAsync_ReturnsHourWithTheMostListens()
    {
        var folder = new Folder { Name = "F", Path = "C:\\Music" };
        var artist = new Artist { Name = "A" };
        var song = CreateSong(folder, artist, "Song", null, TimeSpan.FromMinutes(3));
        // 21:00 UTC and 09:00 UTC are 12 hours apart — guaranteed different local hours in any timezone.
        var eveningUtc = new DateTime(2024, 6, 18, 21, 0, 0, DateTimeKind.Utc);
        var morningUtc = new DateTime(2024, 6, 18, 9, 0, 0, DateTimeKind.Utc);
        var expectedHour = eveningUtc.ToLocalTime().Hour;

        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Folders.Add(folder);
            context.Artists.Add(artist);
            context.Songs.Add(song);
            for (var i = 0; i < 3; i++)
                context.ListenHistory.Add(new ListenHistory { Song = song, ListenTimestampUtc = eveningUtc, EndReason = PlaybackEndReason.Finished, ListenDurationTicks = TimeSpan.FromMinutes(3).Ticks });
            context.ListenHistory.Add(new ListenHistory { Song = song, ListenTimestampUtc = morningUtc, EndReason = PlaybackEndReason.Finished, ListenDurationTicks = TimeSpan.FromMinutes(3).Ticks });
            await context.SaveChangesAsync();
        }

        var result = await _statisticsService.GetPeakListeningHourAsync(new TimeRange(null, null));

        result.Should().Be(expectedHour, "the hour with 3 listens should win over the hour with 1 listen");
    }

    [Fact]
    public async Task GetListeningPatternsAsync_ComputesDayAndHourFromTheSameHistory()
    {
        var folder = new Folder { Name = "F", Path = "C:\\Music" };
        var artist = new Artist { Name = "A" };
        var song = CreateSong(folder, artist, "Song", null, TimeSpan.FromMinutes(3));
        var majorityUtc = new DateTime(2024, 6, 18, 21, 0, 0, DateTimeKind.Utc);
        var minorityUtc = majorityUtc.AddDays(3).AddHours(12);

        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Folders.Add(folder);
            context.Artists.Add(artist);
            context.Songs.Add(song);
            for (var i = 0; i < 3; i++)
                context.ListenHistory.Add(new ListenHistory { Song = song, ListenTimestampUtc = majorityUtc, EndReason = PlaybackEndReason.Finished, ListenDurationTicks = TimeSpan.FromMinutes(3).Ticks });
            context.ListenHistory.Add(new ListenHistory { Song = song, ListenTimestampUtc = minorityUtc, EndReason = PlaybackEndReason.Finished, ListenDurationTicks = TimeSpan.FromMinutes(3).Ticks });
            await context.SaveChangesAsync();
        }

        var result = await _statisticsService.GetListeningPatternsAsync(new TimeRange(null, null));

        result.MostActiveDay.Should().Be(majorityUtc.ToLocalTime().DayOfWeek);
        result.PeakHour.Should().Be(majorityUtc.ToLocalTime().Hour);
    }

    // -------------------------------------------------------------------------
    // Playback source distribution
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GetPlaybackSourceDistributionAsync_GroupsByContextType()
    {
        var folder = new Folder { Name = "F", Path = "C:\\Music" };
        var artist = new Artist { Name = "A" };
        var song = CreateSong(folder, artist, "Song", null, TimeSpan.FromMinutes(3));

        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Folders.Add(folder);
            context.Artists.Add(artist);
            context.Songs.Add(song);
            context.ListenHistory.AddRange(
                new ListenHistory { Song = song, ListenTimestampUtc = DateTime.UtcNow, EndReason = PlaybackEndReason.Finished, ListenDurationTicks = TimeSpan.FromMinutes(2).Ticks, ContextType = PlaybackContextType.Album },
                new ListenHistory { Song = song, ListenTimestampUtc = DateTime.UtcNow, EndReason = PlaybackEndReason.Finished, ListenDurationTicks = TimeSpan.FromMinutes(2).Ticks, ContextType = PlaybackContextType.Album },
                new ListenHistory { Song = song, ListenTimestampUtc = DateTime.UtcNow, EndReason = PlaybackEndReason.Finished, ListenDurationTicks = TimeSpan.FromMinutes(3).Ticks, ContextType = PlaybackContextType.Playlist }
            );
            await context.SaveChangesAsync();
        }

        var result = (await _statisticsService.GetPlaybackSourceDistributionAsync(new TimeRange(null, null))).ToList();

        result.Should().HaveCount(2, "two distinct context types were used");
        var albumStat = result.Single(s => s.Type == PlaybackContextType.Album);
        albumStat.Count.Should().Be(2);
        albumStat.Duration.Should().Be(TimeSpan.FromMinutes(4));
        var playlistStat = result.Single(s => s.Type == PlaybackContextType.Playlist);
        playlistStat.Count.Should().Be(1);
    }

    // -------------------------------------------------------------------------
    // Pagination — global rank correctness
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GetTopSongsAsync_WithOffset_GlobalRankStartsAtOffsetPlusOne()
    {
        // If a user is on page 2 of top songs (offset=2, limit=2), the returned items
        // should have GlobalRank values of 3 and 4 — not 1 and 2.
        var folder = new Folder { Name = "F", Path = "C:\\Music" };
        var artist = new Artist { Name = "A" };
        var songs = Enumerable.Range(1, 5)
            .Select(i => CreateSong(folder, artist, $"Song {i}", null, TimeSpan.FromMinutes(3)))
            .ToList();

        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Folders.Add(folder);
            context.Artists.Add(artist);
            context.Songs.AddRange(songs);
            // Give each song a unique play count so ordering is deterministic
            for (var i = 0; i < songs.Count; i++)
                for (var j = 0; j < songs.Count - i; j++) // song[0] gets 5 plays, song[4] gets 1
                    context.ListenHistory.Add(new ListenHistory { Song = songs[i], ListenTimestampUtc = DateTime.UtcNow, EndReason = PlaybackEndReason.Finished, ListenDurationTicks = TimeSpan.FromMinutes(3).Ticks });
            await context.SaveChangesAsync();
        }

        var page2 = (await _statisticsService.GetTopSongsAsync(new TimeRange(null, null), limit: 2, metric: SortMetric.PlayCount, offset: 2)).ToList();

        page2.Should().HaveCount(2);
        page2[0].GlobalRank.Should().Be(3, "first item on page 2 (offset=2) should have global rank 3");
        page2[1].GlobalRank.Should().Be(4, "second item on page 2 should have global rank 4");
    }

    [Fact]
    public async Task GetTopSongsPageAsync_SearchReturnsCountAndPreservesGlobalRank()
    {
        var folder = new Folder { Name = "F", Path = "C:\\Music" };
        var artist = new Artist { Name = "A" };
        var songs = new[]
        {
            CreateSong(folder, artist, "Other", null, TimeSpan.FromMinutes(3)),
            CreateSong(folder, artist, "MÄTCH A", null, TimeSpan.FromMinutes(3)),
            CreateSong(folder, artist, "MÄTCH B", null, TimeSpan.FromMinutes(3))
        };

        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Folders.Add(folder);
            context.Artists.Add(artist);
            context.Songs.AddRange(songs);
            for (var songIndex = 0; songIndex < songs.Length; songIndex++)
                for (var play = 0; play < 5 - songIndex; play++)
                    context.ListenHistory.Add(new ListenHistory { Song = songs[songIndex], ListenTimestampUtc = DateTime.UtcNow, EndReason = PlaybackEndReason.Finished, ListenDurationTicks = TimeSpan.FromMinutes(3).Ticks });
            await context.SaveChangesAsync();
        }

        var result = await _statisticsService.GetTopSongsPageAsync(
            new TimeRange(null, null),
            limit: 1,
            metric: SortMetric.PlayCount,
            offset: 1,
            searchTerm: "mätch");

        result.TotalCount.Should().Be(2);
        result.Items.Should().ContainSingle();
        result.Items[0].Song.Title.Should().Be("MÄTCH B");
        result.Items[0].GlobalRank.Should().Be(3);
    }

    // -------------------------------------------------------------------------
    // Unique songs played — time range filter
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GetUniqueSongsPlayedAsync_WithTimeRange_ExcludesOutOfRangeListens()
    {
        var folder = new Folder { Name = "F", Path = "C:\\Music" };
        var artist = new Artist { Name = "A" };
        var songInRange = CreateSong(folder, artist, "In Range", null, TimeSpan.FromMinutes(3));
        var songOutOfRange = CreateSong(folder, artist, "Out Of Range", null, TimeSpan.FromMinutes(3));
        var start = new DateTime(2024, 9, 1, 0, 0, 0, DateTimeKind.Utc);

        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Folders.Add(folder);
            context.Artists.Add(artist);
            context.Songs.AddRange(songInRange, songOutOfRange);
            context.ListenHistory.AddRange(
                new ListenHistory { Song = songInRange, ListenTimestampUtc = start.AddDays(1), EndReason = PlaybackEndReason.Finished, ListenDurationTicks = TimeSpan.FromMinutes(3).Ticks },
                new ListenHistory { Song = songOutOfRange, ListenTimestampUtc = start.AddDays(-10), EndReason = PlaybackEndReason.Finished, ListenDurationTicks = TimeSpan.FromMinutes(3).Ticks }
            );
            await context.SaveChangesAsync();
        }

        var count = await _statisticsService.GetUniqueSongsPlayedAsync(new TimeRange(start, null));

        count.Should().Be(1, "only the listen within the time range should contribute to unique songs played");
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
