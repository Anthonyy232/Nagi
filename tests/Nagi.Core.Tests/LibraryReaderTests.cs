using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nagi.Core.Helpers;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;
using Nagi.Core.Services.Implementations;
using Nagi.Core.Tests.Utils;
using NSubstitute;
using Xunit;

namespace Nagi.Core.Tests;

public class LibraryReaderTests : IDisposable
{
    private readonly DbContextFactoryTestHelper _dbHelper = new();
    private readonly ISettingsService _settingsService = Substitute.For<ISettingsService>();
    private readonly LibraryReader _libraryReader;

    public LibraryReaderTests()
    {
        var fileSystem = Substitute.For<IFileSystemService>();
        fileSystem.NormalizePath(Arg.Any<string>()).Returns(call => PathCanonicalizer.Normalize(call.Arg<string>()));
        _libraryReader = new LibraryReader(_dbHelper.ContextFactory, fileSystem, _settingsService,
            Substitute.For<ILogger<LibraryReader>>());
    }

    public void Dispose()
    {
        _libraryReader.Dispose();
        _dbHelper.Dispose();
    }

    [Fact]
    public async Task GetGenreSummariesAsync_CountsSharedSongsAndIncludesEmptyGenres()
    {
        var jazz = new Genre { Name = "Jazz" };
        var rock = new Genre { Name = "Rock" };
        var empty = new Genre { Name = "Empty" };
        var folder = new Folder { Name = "Music", Path = @"C:\Music" };
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Genres.AddRange(jazz, rock, empty);
            context.Songs.AddRange(
                new Song { Folder = folder, FilePath = @"C:\Music\fusion.mp3", Genres = [jazz, rock] },
                new Song { Folder = folder, FilePath = @"C:\Music\jazz.mp3", Genres = [jazz] });
            await context.SaveChangesAsync();
        }

        var summaries = await _libraryReader.GetGenreSummariesAsync();

        summaries.Should().Equal(
            new GenreSummary(empty.Id, "Empty", 0),
            new GenreSummary(jazz.Id, "Jazz", 2),
            new GenreSummary(rock.Id, "Rock", 1));
    }

    [Fact]
    public async Task GetRootFoldersWithCountsAsync_IncludesEmptyRootsAndNestedSongs()
    {
        var music = new Folder { Name = "Music", Path = @"C:\Music" };
        var empty = new Folder { Name = "Empty", Path = @"C:\Empty" };
        var other = new Folder { Name = "Other", Path = @"C:\Other" };
        var child = new Folder { Name = "Album", Path = @"C:\Music\Album", ParentFolder = music };
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Folders.AddRange(music, empty, other, child);
            context.Songs.AddRange(
                new Song { Folder = music, FilePath = @"C:\Music\one.mp3", DirectoryPath = music.Path },
                new Song { Folder = music, FilePath = @"C:\Music\Album\two.mp3", DirectoryPath = child.Path },
                new Song { Folder = other, FilePath = @"C:\Other\three.mp3", DirectoryPath = other.Path });
            await context.SaveChangesAsync();
        }

        var summaries = (await _libraryReader.GetRootFoldersWithCountsAsync()).ToList();

        summaries.ToDictionary(item => item.Folder.Id, item => item.SongCount)
            .Should().BeEquivalentTo(new Dictionary<Guid, int>
            {
                [music.Id] = 2,
                [empty.Id] = 0,
                [other.Id] = 1
            });
        summaries.Should().OnlyContain(item => item.Folder.Songs.Count == 0);
    }

    [Fact]
    public async Task DirectoryQueries_PreserveDriveRootAndIncludeDescendantsOnlyWhenRequested()
    {
        var root = new Folder { Name = "Drive", Path = @"C:\" };
        var direct = new Song { Folder = root, Title = "Song 1", FilePath = @"C:\one.mp3", DirectoryPath = @"C:\" };
        var nested = new Song { Folder = root, Title = "Song 2", FilePath = @"C:\Album\two.mp3", DirectoryPath = @"C:\Album" };
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Songs.AddRange(direct, nested);
            await context.SaveChangesAsync();
        }

        var page = await _libraryReader.GetSongsInDirectoryOffsetAsync(root.Id, root.Path, 0, 10, SongSortOrder.TitleAsc);
        page.TotalCount.Should().Be(1);
        page.Items.Should().ContainSingle().Which.Id.Should().Be(direct.Id);
        var search = await _libraryReader.SearchSongsInFolderOffsetAsync(root.Id, root.Path, "Song", 0, 10, SongSortOrder.TitleAsc);
        search.Items.Should().ContainSingle().Which.Id.Should().Be(direct.Id);
        var recursive = await _libraryReader.GetAllSongIdsInDirectoryRecursiveAsync(root.Id, root.Path, SongSortOrder.TitleAsc);
        recursive.Should().Equal(direct.Id, nested.Id);
    }

    [Fact]
    public async Task GetAllSongsPagedAsync_RequestsSecondPage_ReturnsCorrectSubsetOfSongs()
    {
        var artist = new Artist { Name = "Artist" };
        var folder = new Folder { Name = "Folder", Path = "C:\\" };
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Artists.Add(artist);
            context.Folders.Add(folder);
            for (var i = 1; i <= 25; i++)
            {
                var s = new Song
                {
                    Title = $"Song {i:D2}",
                    FolderId = folder.Id,
                    FilePath = $"C:\\song{i:D2}.mp3"
                };
                s.SongArtists.Add(new SongArtist { ArtistId = artist.Id, Order = 0 });
                s.SyncDenormalizedFields();
                context.Songs.Add(s);
            }
            await context.SaveChangesAsync();
        }

        var result = await _libraryReader.GetAllSongsPagedAsync(2, 10);

        result.Should().NotBeNull();
        result.TotalCount.Should().Be(25);
        result.PageNumber.Should().Be(2);
        result.PageSize.Should().Be(10);
        result.TotalPages.Should().Be(3);
        result.Items.Should().HaveCount(10);
        result.Items.First().Title.Should().Be("Song 11");
        result.Items.Last().Title.Should().Be("Song 20");
    }

    [Fact]
    public async Task GetSongsByIdsAsync_WithLargeBatch_ReturnsAllSongsInChunks()
    {
        var folder = new Folder { Name = "Large", Path = "C:\\Large" };
        var artist = new Artist { Name = "Batch Artist" };
        var songIds = new List<Guid>();

        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Folders.Add(folder);
            context.Artists.Add(artist);

            for (int i = 0; i < 600; i++)
            {
                var song = new Song
                {
                    Title = $"Song {i:D4}",
                    FolderId = folder.Id,
                    FilePath = $"C:\\Large\\song{i:D4}.mp3"
                };
                song.SongArtists.Add(new SongArtist { Artist = artist, Order = 0 });
                song.SyncDenormalizedFields();
                context.Songs.Add(song);
                songIds.Add(song.Id);
            }
            await context.SaveChangesAsync();
        }

        var result = await _libraryReader.GetSongsByIdsAsync(songIds);

        result.Keys.Should().BeEquivalentTo(songIds);
    }

    [Fact]
    public async Task QueueQueries_ReturnGenresAndFullDurationAcrossBatches_WithoutHeavyFields()
    {
        var folder = new Folder { Name = "Queue", Path = "C:\\Queue" };
        var genre = new Genre { Name = "Rock" };
        var artists = new[] { new Artist { Name = "Lead" }, new Artist { Name = "Guest" } };
        var songs = Enumerable.Range(0, 601).Select(i => new Song
        {
            Title = $"Queue {i}", FilePath = $"C:\\Queue\\{i}.mp3", Folder = folder,
            Duration = TimeSpan.FromSeconds(60), Genres = [genre], Lyrics = "Heavy lyrics",
            AlbumArtUriFromTrack = "C:\\cache\\cover.jpg",
            SongArtists = artists.Select((artist, order) => new SongArtist { Artist = artist, Order = order }).ToList()
        }).ToArray();
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Songs.AddRange(songs);
            await context.SaveChangesAsync();
        }
        var ids = songs.Select(s => s.Id).Append(songs[0].Id).Append(Guid.NewGuid()).ToArray();

        var queue = await _libraryReader.GetQueueSongsAsync(ids);
        queue.Should().HaveCount(601);
        queue.Values.Should().OnlyContain(s => s.Genres.Count == 1 && s.Genres.First().Name == "Rock");
        queue.Values.Should().OnlyContain(s => s.Lyrics == null && s.AlbumArtUriFromTrack == null);
        queue[songs[0].Id].SongArtists.OrderBy(sa => sa.Order).Select(sa => sa.Artist.Id)
            .Should().Equal(artists.Select(a => a.Id));
        queue[songs[0].Id].SongArtists.OrderBy(sa => sa.Order).Select(sa => sa.Artist.Name)
            .Should().Equal("Lead", "Guest");
        (await _libraryReader.GetSongsDurationAsync(ids)).Should().Be(TimeSpan.FromMinutes(601));
        (await _libraryReader.GetSongsDurationAsync([Guid.NewGuid()])).Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public async Task GetArtistsForSongAsync_ReturnsOrderedArtists_WithCorrectProjection()
    {
        var folder = new Folder { Name = "Test Folder", Path = "C:\\Test" };
        var artist1 = new Artist { Name = "Second Artist" };
        var artist2 = new Artist { Name = "First Artist" };

        var song = new Song
        {
            Title = "Collaboration",
            Folder = folder,
            FilePath = "C:\\Test\\collab.mp3"
        };

        song.SongArtists.Add(new SongArtist { Artist = artist2, Order = 1 });
        song.SongArtists.Add(new SongArtist { Artist = artist1, Order = 0 });

        song.SyncDenormalizedFields();

        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Folders.Add(folder);
            context.Artists.AddRange(artist1, artist2);
            context.Songs.Add(song);
            await context.SaveChangesAsync();
        }

        var result = (await _libraryReader.GetArtistsForSongAsync(song.Id)).ToList();

        result.Select(artist => (artist.Id, artist.Name)).Should()
            .Equal((artist1.Id, "Second Artist"), (artist2.Id, "First Artist"));
    }

    [Fact]
    public async Task SearchArtistsPagedAsync_WithEmptyTerm_ReturnsAllArtistsOrderedByName()
    {
        await using (var ctx = _dbHelper.ContextFactory.CreateDbContext())
        {
            ctx.Artists.AddRange(
                new Artist { Name = "Zeppelin" },
                new Artist { Name = "AC/DC" },
                new Artist { Name = "Beatles" });
            await ctx.SaveChangesAsync();
        }

        var results = ((await _libraryReader.SearchArtistsPagedAsync("   ", 1, 20)).Items).ToList();

        results.Select(artist => artist.Name).Should().Equal("AC/DC", "Beatles", "Zeppelin");
    }

    [Fact]
    public async Task SearchArtistsPagedAsync_WithMatchingTerm_ReturnsFilteredArtists()
    {
        await using (var ctx = _dbHelper.ContextFactory.CreateDbContext())
        {
            ctx.Artists.AddRange(
                new Artist { Name = "AC/DC" },
                new Artist { Name = "Radiohead" });
            await ctx.SaveChangesAsync();
        }

        var results = ((await _libraryReader.SearchArtistsPagedAsync("radio", 1, 20)).Items).ToList();

        results.Select(artist => artist.Name).Should().Equal("Radiohead");
    }

    [Theory]
    [InlineData(true, "")]
    [InlineData(true, "Collection")]
    [InlineData(false, "")]
    [InlineData(false, "Collection")]
    public async Task SearchAlbumsPagedAsync_PreservesArtistsAndHonorsSortSetting(bool ignoreArticles, string term)
    {
        var ants = new Artist { Name = "The Ants" };
        var bees = new Artist { Name = "Bees" };
        var guest = new Artist { Name = "Guest" };
        var apple = new Album { Title = "The Apple Collection", AlbumArtists = [new() { Artist = ants, Order = 0 }, new() { Artist = guest, Order = 1 }] };
        var banana = new Album { Title = "Banana Collection", AlbumArtists = [new() { Artist = ants, Order = 0 }] };
        var beeAlbum = new Album { Title = "Collection", AlbumArtists = [new() { Artist = bees, Order = 0 }] };
        await using (var ctx = _dbHelper.ContextFactory.CreateDbContext())
        {
            ctx.Albums.AddRange(apple, banana, beeAlbum);
            await ctx.SaveChangesAsync();
        }
        _settingsService.IgnoreLeadingArticlesOnSortEnabledChanged += Raise.Event<Action<bool>>(ignoreArticles);

        var result = await _libraryReader.SearchAlbumsPagedAsync(term, 1, 20);
        var results = result.Items.ToList();

        result.TotalCount.Should().Be(3);
        results.Select(a => a.Id).Should().Equal(ignoreArticles
            ? [apple.Id, banana.Id, beeAlbum.Id] : new[] { beeAlbum.Id, banana.Id, apple.Id });
        results.Single(a => a.Id == apple.Id).AlbumArtists.OrderBy(a => a.Order)
            .Select(a => a.Artist.Name).Should().Equal("The Ants", "Guest");
        var allAlbums = await _libraryReader.GetAllAlbumsPagedAsync(1, 20);
        allAlbums.Items.Select(a => a.Id).Should().Equal(results.Select(a => a.Id));
    }

    [Theory]
    [InlineData("album")]
    [InlineData("artist")]
    [InlineData("genre")]
    [InlineData("folder")]
    public async Task ScopedSongSearch_PagesAndBulkIdsUseSameEscapedAlbumFilter(string scope)
    {
        var folder = new Folder { Name = "Music", Path = @"C:\Music" };
        var artist = new Artist { Name = "Artist" };
        var genre = new Genre { Name = "Rock" };
        var album = new Album { Title = "Needle% Collection" };
        var songs = Enumerable.Range(0, 3).Select(index => new Song
        {
            Title = $"Song {index}", FilePath = $@"C:\Music\{index}.mp3", DirectoryPath = folder.Path,
            Folder = folder, Album = index < 2 ? album : new Album { Title = "NeedleX Collection" },
            TrackNumber = index + 1, DurationTicks = TimeSpan.FromMinutes(1).Ticks,
            SongArtists = [new() { Artist = artist }], Genres = [genre]
        }).ToArray();
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Songs.AddRange(songs);
            await context.SaveChangesAsync();
        }
        const string term = "Needle%";
        const SongSortOrder sort = SongSortOrder.TrackNumberAsc;
        var ids = scope switch
        {
            "album" => await _libraryReader.SearchAllSongIdsInAlbumAsync(album.Id, term, sort),
            "artist" => await _libraryReader.SearchAllSongIdsInArtistAsync(artist.Id, term, sort),
            "genre" => await _libraryReader.SearchAllSongIdsInGenreAsync(genre.Id, term, sort),
            _ => await _libraryReader.SearchAllSongIdsInFolderAsync(folder.Id, folder.Path, term, sort)
        };
        var page = scope switch
        {
            "album" => await _libraryReader.SearchSongsInAlbumPagedAsync(album.Id, term, 2, 1),
            "artist" => await _libraryReader.SearchSongsInArtistPagedAsync(artist.Id, term, 2, 1),
            "genre" => await _libraryReader.SearchSongsInGenrePagedAsync(genre.Id, term, 2, 1),
            _ => await _libraryReader.SearchSongsInFolderOffsetAsync(folder.Id, folder.Path, term, 1, 1, sort)
        };

        ids.Should().Equal(songs[0].Id, songs[1].Id);
        page.TotalCount.Should().Be(2);
        page.Items.Should().ContainSingle().Which.Id.Should().Be(songs[1].Id);
        if (scope == "album")
        {
            (await _libraryReader.GetSearchTotalDurationInAlbumAsync(album.Id, term)).Should().Be(TimeSpan.FromMinutes(2));
            (await _libraryReader.GetSearchTotalDurationInAlbumAsync(album.Id, "Rock")).Should().Be(TimeSpan.Zero);
        }
    }

    [Fact]
    public async Task FolderSearch_BulkIdsExcludeMatchingSongsOutsideTheCurrentDirectory()
    {
        var root = new Folder { Name = "Music", Path = @"C:\Music" };
        var paths = new[] { @"C:\Music\Rock", @"C:\Music\Rock", @"C:\Music", @"C:\Music\Jazz", @"C:\Music\Rock\Live" };
        var songs = paths.Select((path, index) => new Song
        {
            Folder = root, DirectoryPath = path, FilePath = $@"{path}\{index}.mp3",
            Title = $"Match {index}", TrackNumber = index + 1
        }).ToArray();
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Songs.AddRange(songs);
            await context.SaveChangesAsync();
        }

        var ids = await _libraryReader.SearchAllSongIdsInFolderAsync(root.Id, @"C:\Music\Rock\",
            "Match", SongSortOrder.TrackNumberDesc);
        var page = await _libraryReader.SearchSongsInFolderOffsetAsync(root.Id, @"C:\Music\Rock\",
            "Match", 0, 50, SongSortOrder.TrackNumberDesc);

        ids.Should().Equal(songs[1].Id, songs[0].Id);
        page.Items.Select(song => song.Id).Should().Equal(ids);
        page.TotalCount.Should().Be(ids.Count);
    }

    [Fact]
    public async Task SearchAlbumsPagedAsync_WithMatchingTerm_ReturnsFilteredAlbums()
    {
        await using (var ctx = _dbHelper.ContextFactory.CreateDbContext())
        {
            ctx.Albums.AddRange(
                new Album { Title = "Back in Black" },
                new Album { Title = "Dark Side of the Moon" });
            await ctx.SaveChangesAsync();
        }

        var results = ((await _libraryReader.SearchAlbumsPagedAsync("black", 1, 20)).Items).ToList();

        results.Select(album => album.Title).Should().Equal("Back in Black");
    }

    [Fact]
    public async Task GetSongsByAlbumIdPagedAsync_ReturnsSongsOrderedByTrackNumberThenTitle()
    {
        var folder = new Folder { Name = "F", Path = "C:\\Music" };
        var album = new Album { Title = "Dark Side" };
        var song1 = new Song { Title = "Z Title", TrackNumber = 2, Album = album, Folder = folder, FilePath = "C:\\Music\\z.mp3" };
        var song2 = new Song { Title = "A Title", TrackNumber = 2, Album = album, Folder = folder, FilePath = "C:\\Music\\a.mp3" };
        var song3 = new Song { Title = "First", TrackNumber = 1, Album = album, Folder = folder, FilePath = "C:\\Music\\f.mp3" };
        await using (var ctx = _dbHelper.ContextFactory.CreateDbContext())
        {
            ctx.Folders.Add(folder);
            ctx.Albums.Add(album);
            ctx.Songs.AddRange(song1, song2, song3);
            await ctx.SaveChangesAsync();
        }

        var results = ((await _libraryReader.GetSongsByAlbumIdPagedAsync(album.Id, 1, 20, SongSortOrder.TrackNumberAsc)).Items).ToList();

        results.Select(song => song.Title).Should().Equal("First", "A Title", "Z Title");
    }

    [Fact]
    public async Task GetSongsByArtistIdPagedAsync_ReturnsSongsOrderedByAlbumTitleThenTrackNumber()
    {
        var folder = new Folder { Name = "F", Path = "C:\\Music" };
        var artist = new Artist { Name = "AC/DC" };
        var albumB = new Album { Title = "Back in Black" };
        var albumH = new Album { Title = "Highway to Hell" };

        var songB2 = new Song { Title = "Hells Bells", TrackNumber = 2, Album = albumB, Folder = folder, FilePath = "C:\\Music\\b2.mp3" };
        var songB1 = new Song { Title = "Back in Black", TrackNumber = 1, Album = albumB, Folder = folder, FilePath = "C:\\Music\\b1.mp3" };
        var songH1 = new Song { Title = "Highway to Hell", TrackNumber = 1, Album = albumH, Folder = folder, FilePath = "C:\\Music\\h1.mp3" };
        foreach (var s in new[] { songB2, songB1, songH1 })
            s.SongArtists.Add(new SongArtist { Song = s, Artist = artist, Order = 0 });

        await using (var ctx = _dbHelper.ContextFactory.CreateDbContext())
        {
            ctx.Folders.Add(folder);
            ctx.Artists.Add(artist);
            ctx.Albums.AddRange(albumB, albumH);
            ctx.Songs.AddRange(songB2, songB1, songH1);
            await ctx.SaveChangesAsync();
        }

        var results = ((await _libraryReader.GetSongsByArtistIdPagedAsync(artist.Id, 1, 20, SongSortOrder.AlbumAsc)).Items).ToList();

        results.Select(song => song.Title).Should().Equal("Back in Black", "Hells Bells", "Highway to Hell");
    }

    [Fact]
    public async Task GlobalSongSearch_MatchesMetadataAndTreatsWildcardsLiterally()
    {
        var folder = new Folder { Name = "F", Path = "C:\\Music" };
        var creditedArtist = new Artist { Name = "Needle%" };
        var otherArtist = new Artist { Name = "Other" };
        var matches = new[]
        {
            new Song { Title = "Needle%" },
            new Song { SongArtists = [new() { Artist = otherArtist }, new() { Artist = creditedArtist, Order = 1 },
                new() { Artist = new Artist { Name = "Needle% Guest" }, Order = 2 }] },
            new Song { Album = new Album { Title = "Needle%" } },
            new Song { Album = new Album { AlbumArtists = [new() { Artist = otherArtist }, new() { Artist = creditedArtist, Order = 1 }] } },
            new Song { Genres = [new Genre { Name = "Needle%" }] }
        };
        var yearMatch = new Song { Year = 1999 };
        var songs = matches.Concat([yearMatch, new Song { Title = "NeedleX" }, new Song()]).ToArray();
        await using (var ctx = _dbHelper.ContextFactory.CreateDbContext())
        {
            for (var i = 0; i < songs.Length; i++)
            {
                songs[i].Folder = folder;
                songs[i].FilePath = $@"C:\Music\{i}.flac";
            }
            ctx.Songs.AddRange(songs);
            await ctx.SaveChangesAsync();
        }

        var result = await _libraryReader.SearchSongsPagedAsync("needle%", 1, 20);

        result.TotalCount.Should().Be(matches.Length);
        result.Items.Select(song => song.Id).Should().BeEquivalentTo(matches.Select(song => song.Id));
        (await _libraryReader.SearchAllSongIdsAsync("needle%", SongSortOrder.TitleAsc))
            .Should().Equal(result.Items.Select(song => song.Id));
        (await _libraryReader.SearchSongsPagedAsync("1999", 1, 20)).Items
            .Should().ContainSingle().Which.Id.Should().Be(yearMatch.Id);
        (await _libraryReader.SearchSongsPagedAsync("absent", 1, 20)).TotalCount.Should().Be(0);
    }

    [Fact]
    public async Task SearchSongsPagedAsync_WithEmptyTerm_ReturnsAllSongs()
    {
        var folder = new Folder { Name = "F", Path = "C:\\Music" };
        var song1 = new Song { Title = "Alpha", Folder = folder, FilePath = "C:\\Music\\a.mp3" };
        var song2 = new Song { Title = "Beta", Folder = folder, FilePath = "C:\\Music\\b.mp3" };
        await using (var ctx = _dbHelper.ContextFactory.CreateDbContext())
        {
            ctx.Folders.Add(folder);
            ctx.Songs.AddRange(song1, song2);
            await ctx.SaveChangesAsync();
        }

        var result = await _libraryReader.SearchSongsPagedAsync("", 1, 10);

        result.TotalCount.Should().Be(2);
    }

    [Fact]
    public async Task GetSearchTotalDurationInAlbumAsync_WithEmptyTerm_ReturnsTotalAlbumDuration()
    {
        var folder = new Folder { Name = "F", Path = "C:\\Music" };
        var album = new Album { Title = "Test Album" };
        var song1 = new Song { Title = "S1", DurationTicks = TimeSpan.FromMinutes(3).Ticks, Album = album, Folder = folder, FilePath = "C:\\Music\\s1.mp3" };
        var song2 = new Song { Title = "S2", DurationTicks = TimeSpan.FromMinutes(4).Ticks, Album = album, Folder = folder, FilePath = "C:\\Music\\s2.mp3" };
        await using (var ctx = _dbHelper.ContextFactory.CreateDbContext())
        {
            ctx.Folders.Add(folder);
            ctx.Albums.Add(album);
            ctx.Songs.AddRange(song1, song2);
            await ctx.SaveChangesAsync();
        }

        var duration = await _libraryReader.GetSearchTotalDurationInAlbumAsync(album.Id, "   ");

        duration.Should().Be(TimeSpan.FromMinutes(7), "empty term falls back to total album duration");
    }

    [Fact]
    public async Task GetSearchTotalDurationInAlbumAsync_WithMatchingTerm_ReturnsSumOfMatchedSongs()
    {
        var folder = new Folder { Name = "F", Path = "C:\\Music" };
        var album = new Album { Title = "Test Album" };
        var songA = new Song { Title = "Alpha Song", DurationTicks = TimeSpan.FromMinutes(3).Ticks, Album = album, Folder = folder, FilePath = "C:\\Music\\a.mp3" };
        var songB = new Song { Title = "Beta Song", DurationTicks = TimeSpan.FromMinutes(5).Ticks, Album = album, Folder = folder, FilePath = "C:\\Music\\b.mp3" };
        await using (var ctx = _dbHelper.ContextFactory.CreateDbContext())
        {
            ctx.Folders.Add(folder);
            ctx.Albums.Add(album);
            ctx.Songs.AddRange(songA, songB);
            await ctx.SaveChangesAsync();
        }

        var duration = await _libraryReader.GetSearchTotalDurationInAlbumAsync(album.Id, "alpha");

        duration.Should().Be(TimeSpan.FromMinutes(3), "only the matched song's duration is summed");
    }

    [Fact]
    public async Task GetTopAlbumsForArtistAsync_OrdersByPlayCountDescThenSongCount()
    {
        var artist = new Artist { Name = "Test Artist" };
        var albumHigh = new Album { Title = "Popular Album" };
        var albumLow = new Album { Title = "Obscure Album" };
        var albumTied = new Album { Title = "Single Hit" };
        var folder = new Folder { Name = "F", Path = "C:\\Music" };

        var s1 = new Song { Title = "Hit 1", PlayCount = 10, Album = albumHigh, Folder = folder, FilePath = "C:\\Music\\h1.mp3" };
        var s2 = new Song { Title = "Hit 2", PlayCount = 10, Album = albumHigh, Folder = folder, FilePath = "C:\\Music\\h2.mp3" };
        var s3 = new Song { Title = "Deep Cut", PlayCount = 5, Album = albumLow, Folder = folder, FilePath = "C:\\Music\\d1.mp3" };
        var singleHit = new Song { Title = "Hit", PlayCount = 20, Album = albumTied, Folder = folder, FilePath = @"C:\Music\hit.mp3" };

        albumHigh.AlbumArtists.Add(new AlbumArtist { Album = albumHigh, Artist = artist, Order = 0 });
        albumLow.AlbumArtists.Add(new AlbumArtist { Album = albumLow, Artist = artist, Order = 0 });
        albumTied.AlbumArtists.Add(new AlbumArtist { Artist = artist });

        await using (var ctx = _dbHelper.ContextFactory.CreateDbContext())
        {
            ctx.Folders.Add(folder);
            ctx.Artists.Add(artist);
            ctx.Albums.AddRange(albumHigh, albumLow);
            ctx.Songs.AddRange(s1, s2, s3, singleHit);
            await ctx.SaveChangesAsync();
        }

        var results = (await _libraryReader.GetTopAlbumsForArtistAsync(artist.Id, 10)).ToList();

        results.Select(album => album.Id).Should().Equal(albumHigh.Id, albumTied.Id, albumLow.Id);
        (await _libraryReader.GetTopAlbumsForArtistAsync(artist.Id, 1))
            .Should().ContainSingle().Which.Id.Should().Be(albumHigh.Id);
    }

    [Fact]
    public async Task GetTopAlbumsForArtistAsync_IncludesTrackCreditsWithoutDuplicatingAlbumCredits()
    {
        var artist = new Artist { Name = "Guest Artist" };
        var folder = new Folder { Name = "Music", Path = @"C:\Music" };
        var ownAlbum = new Album { Title = "Own Album", AlbumArtists = [new() { Artist = artist }] };
        var compilation = new Album { Title = "Compilation" };
        var unrelated = new Album { Title = "Other Artist" };
        var songs = new[]
        {
            new Song { FilePath = @"C:\Music\own.flac", Album = ownAlbum, SongArtists = [new() { Artist = artist }] },
            new Song { FilePath = @"C:\Music\guest1.flac", Album = compilation, SongArtists = [new() { Artist = artist }] },
            new Song { FilePath = @"C:\Music\guest2.flac", Album = compilation, SongArtists = [new() { Artist = artist }] },
            new Song { FilePath = @"C:\Music\other.flac", Album = unrelated }
        };
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            foreach (var song in songs) song.Folder = folder;
            context.Songs.AddRange(songs);
            await context.SaveChangesAsync();
        }

        var albums = await _libraryReader.GetTopAlbumsForArtistAsync(artist.Id, 10);

        albums.Select(album => album.Id).Should().BeEquivalentTo(new[] { ownAlbum.Id, compilation.Id });
    }

    [Fact]
    public async Task GetSongByFilePathAsync_FindsOnlyMatchingPath()
    {
        var folder = new Folder { Name = "F", Path = "C:\\Music" };
        var song = new Song { Title = "S", Folder = folder, FilePath = "C:\\Music\\unique.mp3" };
        await using (var ctx = _dbHelper.ContextFactory.CreateDbContext())
        {
            ctx.Folders.Add(folder);
            ctx.Songs.Add(song);
            await ctx.SaveChangesAsync();
        }

        var result = await _libraryReader.GetSongByFilePathAsync("C:\\Music\\unique.mp3");

        result.Should().NotBeNull();
        result!.Title.Should().Be("S");
        (await _libraryReader.GetSongByFilePathAsync("   ")).Should().BeNull();
        (await _libraryReader.GetSongByFilePathAsync(@"C:\Music\missing.mp3")).Should().BeNull();
    }

    [Fact]
    public async Task RecentListens_PreservesRepeatedListensAndStablePagingWhenTimestampsMatch()
    {
        var folder = new Folder { Name = "Music", Path = "C:\\Music" };
        var song = new Song { Folder = folder, Title = "Same song", ArtistName = "Artist", FilePath = "C:\\Music\\1.mp3" };
        var time = DateTime.UtcNow;
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Songs.Add(song);
            context.ListenHistory.AddRange(Enumerable.Range(0, 3).Select(_ => new ListenHistory { Song = song, ListenTimestampUtc = time }));
            await context.SaveChangesAsync();
        }

        var first = await _libraryReader.GetRecentListensAsync("Same song", 0, 2);
        var second = await _libraryReader.GetRecentListensAsync("Same song", 2, 2);

        first.Should().HaveCount(2);
        second.Should().ContainSingle();
        first.Concat(second).Select(h => h.Id).Should().BeInDescendingOrder().And.OnlyHaveUniqueItems();
        first.Concat(second).Should().OnlyContain(h => h.SongId == song.Id);
    }

    [Fact]
    public async Task PlaylistReads_PreserveCustomOrderAcrossExportPagingAndSearch()
    {
        var (playlist, songs) = await _dbHelper.SeedPlaylistAsync(3, 1, 2);
        var expectedIds = new[] { songs[1].Id, songs[2].Id, songs[0].Id };
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            var other = new Song { Title = "Other", FolderId = songs[0].FolderId, FilePath = @"C:\other.mp3" };
            context.PlaylistSongs.Add(new PlaylistSong { PlaylistId = playlist.Id, Song = other, Order = 1.25 });
            await context.SaveChangesAsync();
        }

        var exported = await _libraryReader.GetSongsInPlaylistOrderedAsync(playlist.Id);
        exported.Where(s => s.Title.StartsWith("Song")).Select(s => s.Id).Should().Equal(expectedIds);

        var page = await _libraryReader.GetSongsByPlaylistPagedAsync(playlist.Id, 3, 1, SongSortOrder.PlaylistOrder);
        page.TotalCount.Should().Be(4);
        var row = page.Items.Should().ContainSingle().Subject;
        row.Id.Should().Be(songs[2].Id);
        row.Order.Should().Be(2);
        row.Grouping.Should().Be("Group");
        row.Lyrics.Should().BeNull();

        var search = await _libraryReader.SearchSongsInPlaylistPagedAsync(playlist.Id, "Song", 2, 1, SongSortOrder.PlaylistOrder);
        search.TotalCount.Should().Be(3);
        search.Items.Should().ContainSingle().Which.Id.Should().Be(songs[2].Id);
        search.Items.Single().Order.Should().Be(2);
        var ids = await _libraryReader.SearchAllSongIdsInPlaylistAsync(playlist.Id, "Song", SongSortOrder.PlaylistOrder);
        ids.Should().Equal(expectedIds);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(10)]
    public async Task GetRandomAlbumIdAsync_ReturnsAnExistingAlbumOrNullForAnEmptyLibrary(int albumCount)
    {
        var albums = Enumerable.Range(0, albumCount).Select(i => new Album { Title = $"Album {i}" }).ToArray();
        using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Albums.AddRange(albums);
            await context.SaveChangesAsync();
        }

        var result = await _libraryReader.GetRandomAlbumIdAsync();
        if (albumCount == 0)
            result.Should().BeNull();
        else
        {
            result.Should().NotBeNull();
            albums.Select(a => a.Id).Should().Contain(result.Value);
        }
    }

    [Fact]
    public async Task GetRandomFolderIdAsync_IgnoresFoldersWithoutSongs()
    {
        var emptyFolder = new Folder { Name = "Empty", Path = "C:\\Empty" };

        var musicFolder = new Folder { Name = "Music", Path = "C:\\Music" };
        var song = new Song { Title = "Song", Folder = musicFolder, FilePath = "C:\\Music\\song.mp3", DirectoryPath = "C:\\Music" };

        using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Folders.Add(emptyFolder);
            context.Folders.Add(musicFolder);
            context.Songs.Add(song);
            await context.SaveChangesAsync();
        }

        for (int i = 0; i < 5; i++)
        {
            var result = await _libraryReader.GetRandomFolderIdAsync();
            result.Should().Be(musicFolder.Id);
        }
    }
}
