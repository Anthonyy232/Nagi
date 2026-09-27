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
                new ListenHistory { Song = songA, ListenTimestampUtc = start.AddDays(-5), EndReason = PlaybackEndReason.Finished, ListenDurationTicks = TimeSpan.FromMinutes(3).Ticks },
                new ListenHistory { Song = songB, ListenTimestampUtc = start.AddDays(1), EndReason = PlaybackEndReason.Finished, ListenDurationTicks = TimeSpan.FromMinutes(3).Ticks }
            );
            await context.SaveChangesAsync();
        }

        var result = (await _statisticsService.GetTopArtistsAsync(new TimeRange(start, null), 10, SortMetric.PlayCount)).ToList();

        result.Should().ContainSingle();
        result[0].Artist.Id.Should().Be(artistB.Id);
    }

    [Fact]
    public async Task GetTopSongsAsync_WithDurationSortMetric_OrdersByTotalDurationDescending()
    {
        var folder = new Folder { Name = "F", Path = "C:\\Music" };
        var artist = new Artist { Name = "A" };
        var highPlaysSong = CreateSong(folder, artist, "High Plays", null, TimeSpan.FromMinutes(1));
        var longDurationSong = CreateSong(folder, artist, "Long Duration", null, TimeSpan.FromHours(1));

        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Folders.Add(folder);
            context.Artists.Add(artist);
            context.Songs.AddRange(highPlaysSong, longDurationSong);
            for (var i = 0; i < 5; i++)
                context.ListenHistory.Add(new ListenHistory { Song = highPlaysSong, ListenTimestampUtc = DateTime.UtcNow, EndReason = PlaybackEndReason.Finished, ListenDurationTicks = TimeSpan.FromMinutes(1).Ticks });
            context.ListenHistory.Add(new ListenHistory { Song = longDurationSong, ListenTimestampUtc = DateTime.UtcNow, EndReason = PlaybackEndReason.Finished, ListenDurationTicks = TimeSpan.FromHours(1).Ticks });
            await context.SaveChangesAsync();
        }

        var byPlayCount = (await _statisticsService.GetTopSongsAsync(new TimeRange(null, null), 10, SortMetric.PlayCount)).ToList();
        var byDuration = (await _statisticsService.GetTopSongsAsync(new TimeRange(null, null), 10, SortMetric.Duration)).ToList();

        byPlayCount[0].Song.Id.Should().Be(highPlaysSong.Id, "5 plays should rank first when sorting by play count");
        byDuration[0].Song.Id.Should().Be(longDurationSong.Id, "60 min total should rank first when sorting by duration");
    }

    [Fact]
    public async Task GetTopSongsAsync_SongWithOnlyPureSkips_DoesNotAppearInResults()
    {
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
                IsEligibleForScrobbling = false
            });
            await context.SaveChangesAsync();
        }

        var result = (await _statisticsService.GetTopSongsAsync(new TimeRange(null, null), 10, SortMetric.PlayCount)).ToList();

        result.Should().BeEmpty("a song that was only skipped before the threshold should have 0 plays and be excluded");
    }

    [Theory]
    [InlineData(null, 3)]
    [InlineData("mätch", 2)]
    public async Task TopItemCounts_CountDistinctQualifyingEntities(string? search, int expectedCount)
    {
        var folder = new Folder { Name = "F", Path = @"C:\Music" };
        var names = new[] { "MÄTCH A", "MÄTCH B", "Other", "MÄTCH Skipped" };
        var songs = names.Select(name =>
        {
            var artist = new Artist { Name = name };
            var album = new Album { Title = name, ArtistName = name, PrimaryArtistName = name };
            var song = CreateSong(folder, artist, name, album, TimeSpan.FromMinutes(3));
            song.Genres.Add(new Genre { Name = name });
            return song;
        }).ToArray();

        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Songs.AddRange(songs);
            context.ListenHistory.AddRange(songs.Select((song, index) => new ListenHistory
            {
                Song = song,
                ListenTimestampUtc = DateTime.UtcNow,
                EndReason = index == 3 ? PlaybackEndReason.Skipped : PlaybackEndReason.Finished,
                ListenDurationTicks = TimeSpan.FromSeconds(10).Ticks
            }));
            context.ListenHistory.Add(new ListenHistory
            {
                Song = songs[0],
                ListenTimestampUtc = DateTime.UtcNow,
                EndReason = PlaybackEndReason.Finished,
                ListenDurationTicks = songs[0].DurationTicks
            });
            await context.SaveChangesAsync();
        }

        var range = new TimeRange(null, null);
        (await _statisticsService.GetTopSongsCountAsync(range, search)).Should().Be(expectedCount);
        (await _statisticsService.GetTopArtistsCountAsync(range, search)).Should().Be(expectedCount);
        (await _statisticsService.GetTopAlbumsCountAsync(range, search)).Should().Be(expectedCount);
        (await _statisticsService.GetTopGenresCountAsync(range, search)).Should().Be(expectedCount);
    }
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
            for (var i = 0; i < 5; i++)
                context.ListenHistory.Add(new ListenHistory { Song = song1, ListenTimestampUtc = DateTime.UtcNow, EndReason = PlaybackEndReason.Finished, ListenDurationTicks = TimeSpan.FromMinutes(1).Ticks });
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
            for (var i = 0; i < 5; i++)
                context.ListenHistory.Add(new ListenHistory { Song = song1, ListenTimestampUtc = DateTime.UtcNow, EndReason = PlaybackEndReason.Finished, ListenDurationTicks = TimeSpan.FromMinutes(1).Ticks });
            context.ListenHistory.Add(new ListenHistory { Song = song2, ListenTimestampUtc = DateTime.UtcNow, EndReason = PlaybackEndReason.Finished, ListenDurationTicks = TimeSpan.FromHours(1).Ticks });
            await context.SaveChangesAsync();
        }

        var byPlayCount = (await _statisticsService.GetTopGenresAsync(new TimeRange(null, null), 10, SortMetric.PlayCount)).ToList();
        var byDuration = (await _statisticsService.GetTopGenresAsync(new TimeRange(null, null), 10, SortMetric.Duration)).ToList();

        byPlayCount[0].Genre.Id.Should().Be(highPlaysGenre.Id, "5 plays should rank first when sorting by play count");
        byDuration[0].Genre.Id.Should().Be(longDurationGenre.Id, "60 min total should rank first when sorting by duration");
    }

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

    [Fact]
    public async Task GetListeningActivityTimelineAsync_GroupsQualifyingPlaysAndAllDurationByLocalHour()
    {
        var folder = new Folder { Name = "F", Path = "C:\\Music" };
        var artist = new Artist { Name = "A" };
        var song = CreateSong(folder, artist, "Song", null, TimeSpan.FromMinutes(3));
        var firstLocalHour = new DateTime(2024, 6, 18, 10, 0, 0, DateTimeKind.Local);
        var secondLocalHour = firstLocalHour.AddHours(1);

        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Folders.Add(folder);
            context.Artists.Add(artist);
            context.Songs.Add(song);
            context.ListenHistory.AddRange(
                new ListenHistory { Song = song, ListenTimestampUtc = firstLocalHour.AddMinutes(5).ToUniversalTime(), EndReason = PlaybackEndReason.Finished, ListenDurationTicks = TimeSpan.FromMinutes(3).Ticks },
                new ListenHistory { Song = song, ListenTimestampUtc = firstLocalHour.AddMinutes(25).ToUniversalTime(), EndReason = PlaybackEndReason.PausedAndAbandoned, ListenDurationTicks = TimeSpan.FromMinutes(1).Ticks },
                new ListenHistory { Song = song, ListenTimestampUtc = secondLocalHour.AddMinutes(10).ToUniversalTime(), EndReason = PlaybackEndReason.Finished, ListenDurationTicks = TimeSpan.FromMinutes(2).Ticks });
            await context.SaveChangesAsync();
        }

        var result = (await _statisticsService.GetListeningActivityTimelineAsync(
            new TimeRange(null, null),
            ActivityInterval.Hour)).ToList();

        result.Should().HaveCount(2);
        result[0].Timestamp.Should().Be(firstLocalHour);
        result[0].Plays.Should().Be(1);
        result[0].Duration.Should().Be(TimeSpan.FromMinutes(4));
        result[1].Timestamp.Should().Be(secondLocalHour);
        result[1].Plays.Should().Be(1);
        result[1].Duration.Should().Be(TimeSpan.FromMinutes(2));
    }

    [Fact]
    public async Task GetListeningPatternsAsync_WithNoHistory_ReturnsDefaults()
    {
        var result = await _statisticsService.GetListeningPatternsAsync(new TimeRange(null, null));

        result.Should().Be(new ListeningPatternStats(DayOfWeek.Monday, 12));
    }
    [Fact]
    public async Task GetListeningPatternsAsync_ComputesDayAndHourFromTheSameHistory()
    {
        var folder = new Folder { Name = "F", Path = "C:\\Music" };
        var artist = new Artist { Name = "A" };
        var song = CreateSong(folder, artist, "Song", null, TimeSpan.FromMinutes(3));
        var tuesdayTen = new DateTime(2024, 6, 18, 10, 0, 0, DateTimeKind.Local).ToUniversalTime();
        var fridayNine = new DateTime(2024, 6, 21, 9, 0, 0, DateTimeKind.Local).ToUniversalTime();
        var fridayEleven = new DateTime(2024, 6, 21, 11, 0, 0, DateTimeKind.Local).ToUniversalTime();

        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Folders.Add(folder);
            context.Artists.Add(artist);
            context.Songs.Add(song);
            var timestamps = Enumerable.Repeat(tuesdayTen, 4)
                .Concat(Enumerable.Repeat(fridayNine, 3))
                .Concat(Enumerable.Repeat(fridayEleven, 2));
            context.ListenHistory.AddRange(timestamps.Select(timestamp => new ListenHistory
            {
                Song = song,
                ListenTimestampUtc = timestamp,
                EndReason = PlaybackEndReason.Finished,
                ListenDurationTicks = TimeSpan.FromMinutes(3).Ticks
            }));
            await context.SaveChangesAsync();
        }

        var result = await _statisticsService.GetListeningPatternsAsync(new TimeRange(null, null));

        result.MostActiveDay.Should().Be(DayOfWeek.Friday);
        result.PeakHour.Should().Be(10);
    }

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

    [Fact]
    public async Task GetTopSongsAsync_WithOffset_GlobalRankStartsAtOffsetPlusOne()
    {
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
            for (var i = 0; i < songs.Count; i++)
                for (var j = 0; j < songs.Count - i; j++)
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
