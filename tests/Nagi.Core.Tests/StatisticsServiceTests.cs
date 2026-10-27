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

    [Theory]
    [InlineData(PlaybackEndReason.Finished, false, true)]
    [InlineData(PlaybackEndReason.Skipped, true, true)]
    [InlineData(PlaybackEndReason.Skipped, false, false)]
    public async Task GetTopSongsAsync_CountsFinishedOrScrobbleEligibleListensAsPlays(
        PlaybackEndReason endReason, bool isEligibleForScrobbling, bool countsAsPlay)
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
                EndReason = endReason,
                ListenDurationTicks = TimeSpan.FromMinutes(3).Ticks,
                IsEligibleForScrobbling = isEligibleForScrobbling
            });
            await context.SaveChangesAsync();
        }

        var result = (await _statisticsService.GetTopSongsAsync(new TimeRange(null, null), 10, SortMetric.PlayCount)).ToList();

        if (countsAsPlay)
        {
            var stats = result.Should().ContainSingle().Subject;
            stats.Song.Id.Should().Be(song.Id);
            stats.TotalPlays.Should().Be(1);
            stats.TotalDuration.Should().Be(TimeSpan.FromMinutes(3));
        }
        else
        {
            result.Should().BeEmpty();
        }
    }

    [Fact]
    public async Task GetTopAlbumsPageAsync_AlbumlessListensDoNotConsumePageSlots()
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

        var result = await _statisticsService.GetTopAlbumsPageAsync(
            new TimeRange(null, null), 2, SortMetric.PlayCount, 0, null);

        result.Items.Select(item => item.Album.Id).Should().BeEquivalentTo(new[] { firstAlbum.Id, secondAlbum.Id });
        result.TotalCount.Should().Be(2);
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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GetTopSongsAsync_ExcludesListensOutsideTimeRange(bool boundaryIsStart)
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

        var range = boundaryIsStart ? new TimeRange(boundary, null) : new TimeRange(null, boundary);
        var result = (await _statisticsService.GetTopSongsAsync(range, 10, SortMetric.PlayCount)).ToList();

        result.Should().ContainSingle().Which.Song.Id.Should().Be(boundaryIsStart ? newSong.Id : oldSong.Id);
    }

    [Fact]
    public async Task TopItems_WithDurationSortMetric_OrderByTotalDurationDescending()
    {
        var folder = new Folder { Name = "F", Path = "C:\\Music" };
        var artist = new Artist { Name = "A" };
        var highPlaysAlbum = new Album { Title = "High Plays Album", ArtistName = "A", PrimaryArtistName = "A" };
        var longDurationAlbum = new Album { Title = "Long Duration Album", ArtistName = "A", PrimaryArtistName = "A" };
        var highPlaysGenre = new Genre { Name = "Pop" };
        var longDurationGenre = new Genre { Name = "Classical" };
        var highPlaysSong = CreateSong(folder, artist, "High Plays", highPlaysAlbum, TimeSpan.FromMinutes(1));
        var longDurationSong = CreateSong(folder, artist, "Long Duration", longDurationAlbum, TimeSpan.FromHours(1));
        highPlaysSong.Genres.Add(highPlaysGenre);
        longDurationSong.Genres.Add(longDurationGenre);

        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Songs.AddRange(highPlaysSong, longDurationSong);
            for (var i = 0; i < 5; i++)
                context.ListenHistory.Add(new ListenHistory { Song = highPlaysSong, ListenTimestampUtc = DateTime.UtcNow, EndReason = PlaybackEndReason.Finished, ListenDurationTicks = TimeSpan.FromMinutes(1).Ticks });
            context.ListenHistory.Add(new ListenHistory { Song = longDurationSong, ListenTimestampUtc = DateTime.UtcNow, EndReason = PlaybackEndReason.Finished, ListenDurationTicks = TimeSpan.FromHours(1).Ticks });
            await context.SaveChangesAsync();
        }

        var range = new TimeRange(null, null);
        (await _statisticsService.GetTopSongsAsync(range, 10, SortMetric.PlayCount)).First().Song.Id.Should().Be(highPlaysSong.Id);
        (await _statisticsService.GetTopSongsAsync(range, 10, SortMetric.Duration)).First().Song.Id.Should().Be(longDurationSong.Id);
        (await _statisticsService.GetTopAlbumsAsync(range, 10, SortMetric.PlayCount)).First().Album.Id.Should().Be(highPlaysAlbum.Id);
        (await _statisticsService.GetTopAlbumsAsync(range, 10, SortMetric.Duration)).First().Album.Id.Should().Be(longDurationAlbum.Id);
        (await _statisticsService.GetTopGenresAsync(range, 10, SortMetric.PlayCount)).First().Genre.Id.Should().Be(highPlaysGenre.Id);
        (await _statisticsService.GetTopGenresAsync(range, 10, SortMetric.Duration)).First().Genre.Id.Should().Be(longDurationGenre.Id);
    }

    [Theory]
    [InlineData(null, 3, "Other")]
    [InlineData("mätch", 2, "MÄTCH A")]
    public async Task TopItemPages_FilterAndCountDistinctQualifyingEntitiesBeyondPageSize(
        string? search, int expectedCount, string expectedTopName)
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
            // "Other" outranks every match, so the top item shows whether the search filtered it out.
            context.ListenHistory.AddRange(new[] { songs[0], songs[2], songs[2] }.Select(song => new ListenHistory
            {
                Song = song,
                ListenTimestampUtc = DateTime.UtcNow,
                EndReason = PlaybackEndReason.Finished,
                ListenDurationTicks = song.DurationTicks
            }));
            await context.SaveChangesAsync();
        }

        var range = new TimeRange(null, null);
        var songPage = await _statisticsService.GetTopSongsPageAsync(range, 1, SortMetric.PlayCount, 0, search);
        songPage.TotalCount.Should().Be(expectedCount);
        songPage.Items.Should().ContainSingle().Which.Song.Title.Should().Be(expectedTopName);
        var artistPage = await _statisticsService.GetTopArtistsPageAsync(range, 1, SortMetric.PlayCount, 0, search);
        artistPage.TotalCount.Should().Be(expectedCount);
        artistPage.Items.Should().ContainSingle().Which.Artist.Name.Should().Be(expectedTopName);
        var albumPage = await _statisticsService.GetTopAlbumsPageAsync(range, 1, SortMetric.PlayCount, 0, search);
        albumPage.TotalCount.Should().Be(expectedCount);
        albumPage.Items.Should().ContainSingle().Which.Album.Title.Should().Be(expectedTopName);
        var genrePage = await _statisticsService.GetTopGenresPageAsync(range, 1, SortMetric.PlayCount, 0, search);
        genrePage.TotalCount.Should().Be(expectedCount);
        genrePage.Items.Should().ContainSingle().Which.Genre.Name.Should().Be(expectedTopName);
    }

    [Fact]
    public async Task GetTotalListenTimeAsync_SumsEveryListenWithinRange()
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
                new ListenHistory { Song = song, ListenTimestampUtc = start.AddDays(1), EndReason = PlaybackEndReason.Finished, ListenDurationTicks = TimeSpan.FromMinutes(2).Ticks },
                new ListenHistory { Song = song, ListenTimestampUtc = start.AddDays(1), EndReason = PlaybackEndReason.Skipped, ListenDurationTicks = TimeSpan.FromMinutes(3).Ticks },
                new ListenHistory { Song = song, ListenTimestampUtc = start.AddDays(1), EndReason = PlaybackEndReason.Finished, ListenDurationTicks = TimeSpan.FromMinutes(5).Ticks }
            );
            await context.SaveChangesAsync();
        }

        var total = await _statisticsService.GetTotalListenTimeAsync(new TimeRange(start, null));

        total.Should().Be(TimeSpan.FromMinutes(10), "every listen in range counts regardless of end reason");
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
