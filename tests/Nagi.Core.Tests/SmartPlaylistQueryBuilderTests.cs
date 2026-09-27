using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using System.Globalization;
using Nagi.Core.Models;
using Nagi.Core.Services.Data;
using Nagi.Core.Services.Implementations;
using Nagi.Core.Tests.Utils;
using Xunit;

namespace Nagi.Core.Tests;

public class SmartPlaylistQueryBuilderTests : IDisposable
{
    private readonly DbContextFactoryTestHelper _dbHelper;
    private readonly SmartPlaylistQueryBuilder _queryBuilder;

    private readonly Artist _artist1;
    private readonly Artist _artist2;
    private readonly Album _album1;
    private readonly Album _album2;
    private readonly Folder _folder;
    private readonly Genre _genreRock;
    private readonly Genre _genrePop;
    private readonly Genre _genreJazz;
    private readonly Song _song1;
    private readonly Song _song2;
    private readonly Song _song3;
    private readonly Song _song4;
    private readonly Song _song5;

    public SmartPlaylistQueryBuilderTests()
    {
        _dbHelper = new DbContextFactoryTestHelper();
        _queryBuilder = new SmartPlaylistQueryBuilder();

        _artist1 = new Artist { Name = "The Beatles" };
        _artist2 = new Artist { Name = "Pink Floyd" };
        _album1 = new Album { Title = "Abbey Road" };
        _album1.AlbumArtists.Add(new AlbumArtist { Artist = _artist1, Order = 0 });
        _album2 = new Album { Title = "The Wall" };
        _album2.AlbumArtists.Add(new AlbumArtist { Artist = _artist2, Order = 0 });
        _folder = new Folder { Name = "Music", Path = "C:\\Music" };
        _genreRock = new Genre { Name = "Rock" };
        _genrePop = new Genre { Name = "Pop" };
        _genreJazz = new Genre { Name = "Jazz" };

        _song1 = new Song
        {
            Title = "Come Together",
            Album = _album1,
            Folder = _folder,
            FilePath = "C:\\Music\\come_together.mp3",
            PlayCount = 100,
            SkipCount = 5,
            Rating = 5,
            Year = 1969,
            IsLoved = true,
            Duration = TimeSpan.FromMinutes(4.5),
            DateAddedToLibrary = DateTime.UtcNow.AddDays(-10),
            LastPlayedDate = DateTime.UtcNow.AddDays(-1),
            Composer = "Lennon-McCartney",
            Comment = "Classic rock song",
            Grouping = "Favorites",
            Bpm = 82,
            TrackNumber = 1,
            DiscNumber = 1,
            Bitrate = 320,
            SampleRate = 44100,
            FileCreatedDate = DateTime.UtcNow.AddDays(-365),
            FileModifiedDate = DateTime.UtcNow.AddDays(-30)
        };
        _song1.SongArtists.Add(new SongArtist { Artist = _artist1, Order = 0 });
        _song1.Genres.Add(_genreRock);

        _song2 = new Song
        {
            Title = "Something",
            Album = _album1,
            Folder = _folder,
            FilePath = "C:\\Music\\something.mp3",
            PlayCount = 50,
            SkipCount = 10,
            Rating = 4,
            Year = 1969,
            IsLoved = true,
            Duration = TimeSpan.FromMinutes(3),
            DateAddedToLibrary = DateTime.UtcNow.AddDays(-30),
            LastPlayedDate = DateTime.UtcNow.AddDays(-7),
            Composer = "George Harrison",
            Bpm = 66,
            TrackNumber = 2,
            DiscNumber = 1,
            Bitrate = 256,
            SampleRate = 44100,
            FileCreatedDate = DateTime.UtcNow.AddDays(-365),
            FileModifiedDate = DateTime.UtcNow.AddDays(-60)
        };
        _song2.SongArtists.Add(new SongArtist { Artist = _artist1, Order = 0 });
        _song2.Genres.Add(_genreRock);
        _song2.Genres.Add(_genrePop);

        _song3 = new Song
        {
            Title = "Another Brick in the Wall",
            Album = _album2,
            Folder = _folder,
            FilePath = "C:\\Music\\wall.mp3",
            PlayCount = 200,
            SkipCount = 2,
            Rating = 5,
            Year = 1979,
            IsLoved = false,
            Duration = TimeSpan.FromMinutes(6.5),
            DateAddedToLibrary = DateTime.UtcNow.AddDays(-60),
            LastPlayedDate = DateTime.UtcNow.AddDays(-14),
            LrcFilePath = "C:\\Lyrics\\wall.lrc",
            TrackNumber = 5,
            DiscNumber = 2,
            Bitrate = 192,
            SampleRate = 48000,
            FileCreatedDate = DateTime.UtcNow.AddDays(-200),
            FileModifiedDate = DateTime.UtcNow.AddDays(-90)
        };
        _song3.SongArtists.Add(new SongArtist { Artist = _artist2, Order = 0 });
        _song3.Genres.Add(_genreRock);

        _song4 = new Song
        {
            Title = "Let It Be",
            Album = null,
            Folder = _folder,
            FilePath = "C:\\Music\\letitbe.mp3",
            PlayCount = 0,
            SkipCount = 0,
            Rating = null,
            Year = 1970,
            IsLoved = false,
            Duration = TimeSpan.FromMinutes(4),
            DateAddedToLibrary = DateTime.UtcNow.AddDays(-5)
        };
        _song4.SongArtists.Add(new SongArtist { Artist = _artist1, Order = 0 });
        _song4.Genres.Add(_genrePop);

        _song5 = new Song
        {
            Title = "Fly Me to the Moon",
            Album = null,
            Folder = _folder,
            FilePath = "C:\\Music\\flyme.mp3",
            PlayCount = 25,
            SkipCount = 1,
            Year = 1964,
            IsLoved = false,
            Duration = TimeSpan.FromMinutes(2.5),
            DateAddedToLibrary = DateTime.UtcNow.AddDays(-100)
        };
        _song5.Genres.Add(_genreJazz);

        _song1.SyncDenormalizedFields();
        _song2.SyncDenormalizedFields();
        _song3.SyncDenormalizedFields();
        _song4.SyncDenormalizedFields();
        _song5.SyncDenormalizedFields();

        using var context = _dbHelper.ContextFactory.CreateDbContext();
        context.Genres.AddRange(_genreRock, _genrePop, _genreJazz);
        context.Songs.AddRange(_song1, _song2, _song3, _song4, _song5);
        context.SaveChanges();
    }

    public void Dispose()
    {
        _dbHelper.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void BuildQuery_TitleIs_ReturnsExactMatch()
    {

        var playlist = CreatePlaylist(new SmartPlaylistRule
        {
            Field = SmartPlaylistField.Title,
            Operator = SmartPlaylistOperator.Is,
            Value = "Come Together"
        });

        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        results.Should().HaveCount(1);
        results[0].Title.Should().Be("Come Together");
    }

    [Fact]
    public void BuildQuery_TitleIs_IsCaseInsensitive()
    {

        var playlist = CreatePlaylist(new SmartPlaylistRule
        {
            Field = SmartPlaylistField.Title,
            Operator = SmartPlaylistOperator.Is,
            Value = "come together"
        });

        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        results.Should().HaveCount(1);
        results[0].Title.Should().Be("Come Together");
    }

    [Fact]
    public void BuildQuery_TitleIsNot_ExcludesMatch()
    {

        var playlist = CreatePlaylist(new SmartPlaylistRule
        {
            Field = SmartPlaylistField.Title,
            Operator = SmartPlaylistOperator.IsNot,
            Value = "Come Together"
        });

        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        results.Should().HaveCount(4);
        results.Should().NotContain(s => s.Title == "Come Together");
    }

    [Fact]
    public void BuildQuery_TitleContains_ReturnsPartialMatches()
    {

        var playlist = CreatePlaylist(new SmartPlaylistRule
        {
            Field = SmartPlaylistField.Title,
            Operator = SmartPlaylistOperator.Contains,
            Value = "the"
        });

        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        results.Should().HaveCount(3);
        results.Should().Contain(s => s.Title == "Come Together");
        results.Should().Contain(s => s.Title == "Another Brick in the Wall");
        results.Should().Contain(s => s.Title == "Fly Me to the Moon");
    }

    [Fact]
    public void BuildQuery_TitleDoesNotContain_ExcludesPartialMatches()
    {

        var playlist = CreatePlaylist(new SmartPlaylistRule
        {
            Field = SmartPlaylistField.Title,
            Operator = SmartPlaylistOperator.DoesNotContain,
            Value = "the"
        });

        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        results.Should().HaveCount(2);
        results.Should().Contain(s => s.Title == "Something");
        results.Should().Contain(s => s.Title == "Let It Be");
    }

    [Fact]
    public void BuildQuery_TitleStartsWith_ReturnsMatchingPrefix()
    {

        var playlist = CreatePlaylist(new SmartPlaylistRule
        {
            Field = SmartPlaylistField.Title,
            Operator = SmartPlaylistOperator.StartsWith,
            Value = "Let"
        });

        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        results.Should().HaveCount(1);
        results[0].Title.Should().Be("Let It Be");
    }

    [Fact]
    public void BuildQuery_TitleEndsWith_ReturnsMatchingSuffix()
    {

        var playlist = CreatePlaylist(new SmartPlaylistRule
        {
            Field = SmartPlaylistField.Title,
            Operator = SmartPlaylistOperator.EndsWith,
            Value = "Moon"
        });

        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        results.Should().HaveCount(1);
        results[0].Title.Should().Be("Fly Me to the Moon");
    }

    [Fact]
    public void BuildQuery_ArtistIs_ReturnsMatchingArtist()
    {

        var playlist = CreatePlaylist(new SmartPlaylistRule
        {
            Field = SmartPlaylistField.Artist,
            Operator = SmartPlaylistOperator.Is,
            Value = "The Beatles"
        });

        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        results.Should().HaveCount(3);
        results.Should().OnlyContain(s => s.ArtistName == "The Beatles");
    }

    [Fact]
    public void BuildQuery_ArtistIsNot_IncludesSongsWithNullArtist()
    {

        var playlist = CreatePlaylist(new SmartPlaylistRule
        {
            Field = SmartPlaylistField.Artist,
            Operator = SmartPlaylistOperator.IsNot,
            Value = "The Beatles"
        });

        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        results.Should().HaveCount(2);
        results.Should().Contain(s => s.Title == "Another Brick in the Wall");
        results.Should().Contain(s => s.Title == "Fly Me to the Moon");
    }

    [Fact]
    public void BuildQuery_AlbumContains_ReturnsMatchingAlbums()
    {

        var playlist = CreatePlaylist(new SmartPlaylistRule
        {
            Field = SmartPlaylistField.Album,
            Operator = SmartPlaylistOperator.Contains,
            Value = "Road"
        });

        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        results.Should().HaveCount(2);
        results.Should().OnlyContain(s => s.Album!.Title == "Abbey Road");
    }

    [Fact]
    public void BuildQuery_ArtistIs_FindsSecondaryArtist()
    {

        var artist3 = new Artist { Name = "Secondary Artist" };
        var songWithTwoArtists = new Song
        {
            Title = "Collaboration",
            AlbumId = _album1.Id,
            FolderId = _folder.Id,
            FilePath = "C:\\Music\\collab.mp3"
        };
        songWithTwoArtists.SongArtists.Add(new SongArtist { ArtistId = _artist1.Id, Order = 0 });
        songWithTwoArtists.SongArtists.Add(new SongArtist { Artist = artist3, Order = 1 });

        songWithTwoArtists.SyncDenormalizedFields();

        using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Artists.Add(artist3);
            context.Songs.Add(songWithTwoArtists);
            context.SaveChanges();
        }

        var playlist = CreatePlaylist(new SmartPlaylistRule
        {
            Field = SmartPlaylistField.Artist,
            Operator = SmartPlaylistOperator.Is,
            Value = "Secondary Artist"
        });

        using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            var results = _queryBuilder.BuildQuery(context, playlist).ToList();

            results.Should().HaveCount(1);
            results[0].Title.Should().Be("Collaboration");
        }
    }

    [Fact]
    public void BuildQuery_WithSearchTerm_FindsByAlbumArtist()
    {

        var albumArtist2 = new Artist { Name = "Different Album Artist" };
        var albumWithDifferentArtist = new Album { Title = "Special Album" };
        albumWithDifferentArtist.AlbumArtists.Add(new AlbumArtist { Artist = albumArtist2, Order = 0 });

        var songWithDifferentAlbumArtist = new Song
        {
            Title = "Album Track",
            Album = albumWithDifferentArtist,
            FolderId = _folder.Id,
            FilePath = "C:\\Music\\albumtrack.mp3"
        };
        songWithDifferentAlbumArtist.SongArtists.Add(new SongArtist { ArtistId = _artist1.Id, Order = 0 });

        songWithDifferentAlbumArtist.SyncDenormalizedFields();
        albumWithDifferentArtist.SyncDenormalizedFields();

        using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Artists.Add(albumArtist2);
            context.Albums.Add(albumWithDifferentArtist);
            context.Songs.Add(songWithDifferentAlbumArtist);
            context.SaveChanges();
        }

        var playlist = CreatePlaylist();

        using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            var results = _queryBuilder.BuildQuery(context, playlist, "Different Album Artist").ToList();

            results.Should().HaveCount(1);
            results[0].Title.Should().Be("Album Track");
        }
    }

    [Theory]
    [InlineData(SmartPlaylistOperator.StartsWith, "rO")]
    [InlineData(SmartPlaylistOperator.EndsWith, "cK")]
    public void BuildQuery_GenrePrefixOrSuffix_FiltersSongsAcrossAllTheirGenres(
        SmartPlaylistOperator ruleOperator, string value)
    {
        var playlist = CreatePlaylist(new SmartPlaylistRule
        {
            Field = SmartPlaylistField.Genre,
            Operator = ruleOperator,
            Value = value
        });
        using var context = _dbHelper.ContextFactory.CreateDbContext();

        var results = _queryBuilder.BuildQuery(context, playlist).Select(song => song.Id).ToList();

        results.Should().BeEquivalentTo(new[] { _song1.Id, _song2.Id, _song3.Id });
    }

    [Fact]
    public void BuildQuery_GenreIs_ReturnsMatchingGenre()
    {

        var playlist = CreatePlaylist(new SmartPlaylistRule
        {
            Field = SmartPlaylistField.Genre,
            Operator = SmartPlaylistOperator.Is,
            Value = "Rock"
        });

        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        results.Should().HaveCount(3);
        results.Should().OnlyContain(s => s.Genres.Any(g => g.Name == "Rock"));
    }

    [Fact]
    public void BuildQuery_GenreIsNot_ExcludesGenre()
    {

        var playlist = CreatePlaylist(new SmartPlaylistRule
        {
            Field = SmartPlaylistField.Genre,
            Operator = SmartPlaylistOperator.IsNot,
            Value = "Rock"
        });

        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        results.Should().HaveCount(2);
        results.Should().NotContain(s => s.Genres.Any(g => g.Name == "Rock"));
    }

    [Fact]
    public void BuildQuery_GenreContains_ReturnsPartialMatch()
    {

        var playlist = CreatePlaylist(new SmartPlaylistRule
        {
            Field = SmartPlaylistField.Genre,
            Operator = SmartPlaylistOperator.Contains,
            Value = "az"
        });

        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        results.Should().HaveCount(1);
        results[0].Title.Should().Be("Fly Me to the Moon");
    }

    [Fact]
    public void BuildQuery_GenreIs_IsCaseInsensitive()
    {

        var playlist = CreatePlaylist(new SmartPlaylistRule
        {
            Field = SmartPlaylistField.Genre,
            Operator = SmartPlaylistOperator.Is,
            Value = "rock"
        });

        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        results.Should().HaveCount(3);
    }

    [Fact]
    public void BuildQuery_PlayCountEquals_ReturnsExactMatch()
    {

        var playlist = CreatePlaylist(new SmartPlaylistRule
        {
            Field = SmartPlaylistField.PlayCount,
            Operator = SmartPlaylistOperator.Equals,
            Value = "100"
        });

        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        results.Should().HaveCount(1);
        results[0].PlayCount.Should().Be(100);
    }

    [Fact]
    public void BuildQuery_PlayCountGreaterThan_ReturnsMatchingSongs()
    {

        var playlist = CreatePlaylist(new SmartPlaylistRule
        {
            Field = SmartPlaylistField.PlayCount,
            Operator = SmartPlaylistOperator.GreaterThan,
            Value = "50"
        });

        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        results.Should().HaveCount(2);
        results.Should().OnlyContain(s => s.PlayCount > 50);
    }

    [Fact]
    public void BuildQuery_PlayCountLessThanOrEqual_ReturnsMatchingSongs()
    {

        var playlist = CreatePlaylist(new SmartPlaylistRule
        {
            Field = SmartPlaylistField.PlayCount,
            Operator = SmartPlaylistOperator.LessThanOrEqual,
            Value = "25"
        });

        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        results.Should().HaveCount(2);
        results.Should().OnlyContain(s => s.PlayCount <= 25);
    }

    [Fact]
    public void BuildQuery_PlayCountInRange_ReturnsMatchingSongs()
    {

        var playlist = CreatePlaylist(new SmartPlaylistRule
        {
            Field = SmartPlaylistField.PlayCount,
            Operator = SmartPlaylistOperator.IsInRange,
            Value = "40",
            SecondValue = "150"
        });

        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        results.Should().HaveCount(2);
        results.Should().OnlyContain(s => s.PlayCount >= 40 && s.PlayCount <= 150);
    }

    [Fact]
    public void BuildQuery_RatingEquals_ReturnsNullableMatch()
    {

        var playlist = CreatePlaylist(new SmartPlaylistRule
        {
            Field = SmartPlaylistField.Rating,
            Operator = SmartPlaylistOperator.Equals,
            Value = "5"
        });

        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        results.Should().HaveCount(2);
        results.Should().OnlyContain(s => s.Rating == 5);
    }

    [Fact]
    public void BuildQuery_YearInRange_ReturnsMatchingSongs()
    {

        var playlist = CreatePlaylist(new SmartPlaylistRule
        {
            Field = SmartPlaylistField.Year,
            Operator = SmartPlaylistOperator.IsInRange,
            Value = "1965",
            SecondValue = "1970"
        });

        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        results.Should().HaveCount(3);
        results.Should().OnlyContain(s => s.Year >= 1965 && s.Year <= 1970);
    }

    [Fact]
    public void BuildQuery_InvalidNumericValue_ReturnsNoFilter()
    {

        var playlist = CreatePlaylist(new SmartPlaylistRule
        {
            Field = SmartPlaylistField.PlayCount,
            Operator = SmartPlaylistOperator.Equals,
            Value = "not-a-number"
        });

        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        results.Should().HaveCount(5);
    }

    [Theory]
    [InlineData("en-US", "180.5")]
    [InlineData("de-DE", "180.5")]
    [InlineData("de-DE", "180,5")]
    public void BuildQuery_DurationDecimals_AcceptsStoredAndLegacyLocalValues(string culture, string value)
    {
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            var playlist = CreatePlaylist(new SmartPlaylistRule
            {
                Field = SmartPlaylistField.Duration, Operator = SmartPlaylistOperator.LessThan, Value = value
            });
            using var context = _dbHelper.ContextFactory.CreateDbContext();

            _queryBuilder.BuildQuery(context, playlist).Select(song => song.Id).ToList()
                .Should().BeEquivalentTo(new[] { _song2.Id, _song5.Id });
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Fact]
    public void BuildQuery_DurationGreaterThan_ReturnsMatchingSongs()
    {

        var playlist = CreatePlaylist(new SmartPlaylistRule
        {
            Field = SmartPlaylistField.Duration,
            Operator = SmartPlaylistOperator.GreaterThan,
            Value = "240"
        });

        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        results.Should().HaveCount(2);
        results.Should().OnlyContain(s => s.Duration > TimeSpan.FromSeconds(240));
    }

    [Fact]
    public void BuildQuery_DurationInRange_ReturnsMatchingSongs()
    {

        var playlist = CreatePlaylist(new SmartPlaylistRule
        {
            Field = SmartPlaylistField.Duration,
            Operator = SmartPlaylistOperator.IsInRange,
            Value = "180",
            SecondValue = "300"
        });

        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        results.Should().HaveCount(3);
    }

    [Fact]
    public void BuildQuery_IsLovedTrue_ReturnsLovedSongs()
    {

        var playlist = CreatePlaylist(new SmartPlaylistRule
        {
            Field = SmartPlaylistField.IsLoved,
            Operator = SmartPlaylistOperator.IsTrue
        });

        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        results.Should().HaveCount(2);
        results.Should().OnlyContain(s => s.IsLoved);
    }

    [Fact]
    public void BuildQuery_IsLovedFalse_ReturnsNotLovedSongs()
    {

        var playlist = CreatePlaylist(new SmartPlaylistRule
        {
            Field = SmartPlaylistField.IsLoved,
            Operator = SmartPlaylistOperator.IsFalse
        });

        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        results.Should().HaveCount(3);
        results.Should().OnlyContain(s => !s.IsLoved);
    }

    [Fact]
    public void BuildQuery_HasLyrics_ReturnsSongsWithLrcFile()
    {

        var playlist = CreatePlaylist(new SmartPlaylistRule
        {
            Field = SmartPlaylistField.HasLyrics,
            Operator = SmartPlaylistOperator.IsTrue
        });

        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        results.Should().HaveCount(1);
        results[0].Title.Should().Be("Another Brick in the Wall");
    }

    [Fact]
    public void BuildQuery_DateAddedInTheLast_ReturnsRecentSongs()
    {

        var playlist = CreatePlaylist(new SmartPlaylistRule
        {
            Field = SmartPlaylistField.DateAdded,
            Operator = SmartPlaylistOperator.IsInTheLast,
            Value = "15"
        });

        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        results.Should().HaveCount(2);
    }

    [Fact]
    public void BuildQuery_DateAddedNotInTheLast_ReturnsOlderSongs()
    {

        var playlist = CreatePlaylist(new SmartPlaylistRule
        {
            Field = SmartPlaylistField.DateAdded,
            Operator = SmartPlaylistOperator.IsNotInTheLast,
            Value = "15"
        });

        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        results.Should().HaveCount(3);
    }

    [Fact]
    public void BuildQuery_SortByTitleAsc_ReturnsSortedResults()
    {

        var playlist = new SmartPlaylist { SortOrder = SmartPlaylistSortOrder.TitleAsc };

        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        results.Should().HaveCount(5);
        results[0].Title.Should().Be("Another Brick in the Wall");
        results[1].Title.Should().Be("Come Together");
        results[4].Title.Should().Be("Something");
    }

    [Fact]
    public void BuildQuery_SortByTitleDesc_ReturnsSortedResults()
    {

        var playlist = new SmartPlaylist { SortOrder = SmartPlaylistSortOrder.TitleDesc };

        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        results[0].Title.Should().Be("Something");
        results[4].Title.Should().Be("Another Brick in the Wall");
    }

    [Fact]
    public void BuildQuery_SortByPlayCountDesc_ReturnsSortedResults()
    {

        var playlist = new SmartPlaylist { SortOrder = SmartPlaylistSortOrder.PlayCountDesc };

        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        results[0].PlayCount.Should().Be(200);
        results[1].PlayCount.Should().Be(100);
    }

    [Fact]
    public void BuildQuery_MatchAllRulesTrue_AppliesAndLogic()
    {

        var playlist = CreatePlaylist(
            matchAllRules: true,
            new SmartPlaylistRule
            {
                Field = SmartPlaylistField.IsLoved,
                Operator = SmartPlaylistOperator.IsTrue
            },
            new SmartPlaylistRule
            {
                Field = SmartPlaylistField.Artist,
                Operator = SmartPlaylistOperator.Is,
                Value = "The Beatles"
            });

        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        results.Should().HaveCount(2);
        results.Should().OnlyContain(s => s.IsLoved && s.ArtistName == "The Beatles");
    }

    [Fact]
    public void BuildQuery_MatchAllRulesFalse_AppliesOrLogic()
    {

        var playlist = CreatePlaylist(
            matchAllRules: false,
            new SmartPlaylistRule
            {
                Field = SmartPlaylistField.Genre,
                Operator = SmartPlaylistOperator.Is,
                Value = "Jazz"
            },
            new SmartPlaylistRule
            {
                Field = SmartPlaylistField.Year,
                Operator = SmartPlaylistOperator.Equals,
                Value = "1979"
            });

        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        results.Should().HaveCount(2);
    }

    [Fact]
    public void BuildQuery_NoRules_ReturnsAllSongs()
    {

        var playlist = CreatePlaylist();

        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        results.Should().HaveCount(5);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void BuildQuery_EmptyContainsValue_MatchesAllTitles(string? value)
    {
        var playlist = CreatePlaylist(new SmartPlaylistRule
        {
            Field = SmartPlaylistField.Title, Operator = SmartPlaylistOperator.Contains, Value = value
        });
        using var context = _dbHelper.ContextFactory.CreateDbContext();

        _queryBuilder.BuildQuery(context, playlist).Count().Should().Be(5);
    }

    [Fact]
    public void BuildQuery_WithSearchTerm_AppliesAdditionalFilter()
    {

        var playlist = CreatePlaylist(new SmartPlaylistRule
        {
            Field = SmartPlaylistField.Genre,
            Operator = SmartPlaylistOperator.Is,
            Value = "Rock"
        });

        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist, searchTerm: "beatles").ToList();

        results.Should().HaveCount(2);
        results.Should().OnlyContain(s => s.ArtistName.Contains("Beatles", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildQuery_SearchTreatsPercentAndUnderscoreAsLiteralCharacters()
    {
        var songs = new[]
        {
            new Song { Title = "100% Real", FolderId = _folder.Id, FilePath = @"C:\Music\percent.mp3" },
            new Song { Title = "100X Real", FolderId = _folder.Id, FilePath = @"C:\Music\letter.mp3" },
            new Song { Title = "Under_score", FolderId = _folder.Id, FilePath = @"C:\Music\underscore.mp3" },
            new Song { Title = "UnderXscore", FolderId = _folder.Id, FilePath = @"C:\Music\other.mp3" },
            new Song { Title = "Bang! Now", FolderId = _folder.Id, FilePath = @"C:\Music\bang.mp3" },
            new Song { Title = "BangX Now", FolderId = _folder.Id, FilePath = @"C:\Music\bang-other.mp3" }
        };
        foreach (var song in songs) song.SyncDenormalizedFields();

        using (var seedContext = _dbHelper.ContextFactory.CreateDbContext())
        {
            seedContext.Songs.AddRange(songs);
            seedContext.SaveChanges();
        }

        var playlist = CreatePlaylist();
        using var context = _dbHelper.ContextFactory.CreateDbContext();

        _queryBuilder.BuildQuery(context, playlist, searchTerm: "%").Select(song => song.Title)
            .Should().Equal("100% Real");
        _queryBuilder.BuildQuery(context, playlist, searchTerm: "_").Select(song => song.Title)
            .Should().Equal("Under_score");
        _queryBuilder.BuildQuery(context, playlist, searchTerm: "!").Select(song => song.Title)
            .Should().Equal("Bang! Now");
    }

    [Fact]
    public void BuildCountQuery_ReturnsCorrectCount()
    {

        var playlist = CreatePlaylist(new SmartPlaylistRule
        {
            Field = SmartPlaylistField.PlayCount,
            Operator = SmartPlaylistOperator.GreaterThan,
            Value = "50"
        });

        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var count = _queryBuilder.BuildCountQuery(context, playlist).Count();

        count.Should().Be(2);
    }

    [Fact]
    public void BuildQuery_ComposerDoesNotContain_IncludesNullComposers()
    {

        var playlist = CreatePlaylist(new SmartPlaylistRule
        {
            Field = SmartPlaylistField.Composer,
            Operator = SmartPlaylistOperator.DoesNotContain,
            Value = "Harrison"
        });

        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        results.Should().HaveCount(4);
        results.Should().NotContain(s => s.Composer != null && s.Composer.Contains("Harrison"));
    }

    [Fact]
    public void BuildQuery_SkipCountGreaterThan_ReturnsMatchingSongs()
    {

        var playlist = CreatePlaylist(new SmartPlaylistRule
        {
            Field = SmartPlaylistField.SkipCount,
            Operator = SmartPlaylistOperator.GreaterThan,
            Value = "3"
        });

        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        results.Should().HaveCount(2);
        results.Should().OnlyContain(s => s.SkipCount > 3);
    }

    [Fact]
    public void BuildQuery_BpmGreaterThan_ReturnsMatchingSongsWithDouble()
    {

        var playlist = CreatePlaylist(new SmartPlaylistRule
        {
            Field = SmartPlaylistField.Bpm,
            Operator = SmartPlaylistOperator.GreaterThan,
            Value = "70"
        });

        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        results.Should().HaveCount(1);
        results[0].Title.Should().Be("Come Together");
    }

    [Fact]
    public void BuildQuery_BpmEquals_UsesApproximateComparison()
    {

        var playlist = CreatePlaylist(new SmartPlaylistRule
        {
            Field = SmartPlaylistField.Bpm,
            Operator = SmartPlaylistOperator.Equals,
            Value = "82"
        });

        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        results.Should().HaveCount(1);
        results[0].Bpm.Should().Be(82);
    }

    [Fact]
    public void BuildQuery_LastPlayedIsNull_HandlesNeverPlayedSongs()
    {

        var playlist = CreatePlaylist(new SmartPlaylistRule
        {
            Field = SmartPlaylistField.LastPlayed,
            Operator = SmartPlaylistOperator.IsNotInTheLast,
            Value = "5"
        });

        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        results.Should().HaveCount(4);
    }

    [Fact]
    public void BuildQuery_SongWithMultipleGenres_MatchesOnAnyGenre()
    {

        var playlist = CreatePlaylist(new SmartPlaylistRule
        {
            Field = SmartPlaylistField.Genre,
            Operator = SmartPlaylistOperator.Is,
            Value = "Pop"
        });

        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        results.Should().HaveCount(2);
        results.Should().Contain(s => s.Title == "Something");
        results.Should().Contain(s => s.Title == "Let It Be");
    }

    [Fact]
    public void BuildQuery_RangeWithEqualMinMax_ReturnsSingleValue()
    {

        var playlist = CreatePlaylist(new SmartPlaylistRule
        {
            Field = SmartPlaylistField.Year,
            Operator = SmartPlaylistOperator.IsInRange,
            Value = "1969",
            SecondValue = "1969"
        });

        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        results.Should().HaveCount(2);
        results.Should().OnlyContain(s => s.Year == 1969);
    }

    [Fact]
    public void BuildQuery_MixedValidAndInvalidRules_AppliesOnlyValidRules()
    {

        var playlist = CreatePlaylist(
            matchAllRules: true,
            new SmartPlaylistRule
            {
                Field = SmartPlaylistField.Genre,
                Operator = SmartPlaylistOperator.Is,
                Value = "Rock"
            },
            new SmartPlaylistRule
            {
                Field = SmartPlaylistField.PlayCount,
                Operator = SmartPlaylistOperator.GreaterThan,
                Value = "not-a-number"
            });

        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        results.Should().HaveCount(3);
    }

    [Fact]
    public void BuildQuery_SortByRandom_PreservesMatchingSongs()
    {

        var playlist = new SmartPlaylist { SortOrder = SmartPlaylistSortOrder.Random };

        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();
        results.Should().HaveCount(5);
    }

    [Fact]
    public void BuildQuery_UnsupportedOperatorForField_IgnoresRule()
    {

        var playlist = CreatePlaylist(new SmartPlaylistRule
        {
            Field = SmartPlaylistField.IsLoved,
            Operator = SmartPlaylistOperator.Contains,
            Value = "true"
        });

        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        results.Should().HaveCount(5);
    }

    [Fact]
    public void BuildQuery_DateGreaterThan_ReturnsMatchingSongs()
    {

        var targetDate = DateTime.UtcNow.AddDays(-20).ToString("yyyy-MM-dd");
        var playlist = CreatePlaylist(new SmartPlaylistRule
        {
            Field = SmartPlaylistField.DateAdded,
            Operator = SmartPlaylistOperator.GreaterThan,
            Value = targetDate
        });

        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        results.Should().HaveCount(2);
    }

    [Fact]
    public void BuildQuery_OrLogicWithMultipleGenreRules_MatchesEither()
    {

        var playlist = CreatePlaylist(
            matchAllRules: false,
            new SmartPlaylistRule
            {
                Field = SmartPlaylistField.Genre,
                Operator = SmartPlaylistOperator.Is,
                Value = "Jazz"
            },
            new SmartPlaylistRule
            {
                Field = SmartPlaylistField.Genre,
                Operator = SmartPlaylistOperator.Is,
                Value = "Pop"
            });

        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        results.Should().HaveCount(3);
    }

    [Fact]
    public void BuildQuery_AndLogicWithContradictoryRules_ReturnsEmpty()
    {

        var playlist = CreatePlaylist(
            matchAllRules: true,
            new SmartPlaylistRule
            {
                Field = SmartPlaylistField.Year,
                Operator = SmartPlaylistOperator.Equals,
                Value = "1969"
            },
            new SmartPlaylistRule
            {
                Field = SmartPlaylistField.Year,
                Operator = SmartPlaylistOperator.Equals,
                Value = "1979"
            });

        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        results.Should().BeEmpty();
    }

    [Fact]
    public void BuildQuery_EmptySearchTerm_DoesNotFilter()
    {

        var playlist = CreatePlaylist(new SmartPlaylistRule
        {
            Field = SmartPlaylistField.Genre,
            Operator = SmartPlaylistOperator.Is,
            Value = "Rock"
        });

        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var resultsNoSearch = _queryBuilder.BuildQuery(context, playlist, searchTerm: null).ToList();
        var resultsEmptySearch = _queryBuilder.BuildQuery(context, playlist, searchTerm: "").ToList();
        var resultsWhitespaceSearch = _queryBuilder.BuildQuery(context, playlist, searchTerm: "   ").ToList();

        resultsNoSearch.Should().HaveCount(3);
        resultsEmptySearch.Should().HaveCount(3);
        resultsWhitespaceSearch.Should().HaveCount(3);
    }

    [Fact]
    public void BuildQuery_TrackNumberGreaterThan_ReturnsMatchingSongs()
    {

        var playlist = CreatePlaylist(new SmartPlaylistRule
        {
            Field = SmartPlaylistField.TrackNumber,
            Operator = SmartPlaylistOperator.GreaterThan,
            Value = "1"
        });

        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        results.Should().HaveCount(2);
        results.Should().OnlyContain(s => s.TrackNumber > 1);
    }

    [Fact]
    public void BuildQuery_DiscNumberEquals_ReturnsMatchingSongs()
    {

        var playlist = CreatePlaylist(new SmartPlaylistRule
        {
            Field = SmartPlaylistField.DiscNumber,
            Operator = SmartPlaylistOperator.Equals,
            Value = "2"
        });

        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        results.Should().HaveCount(1);
        results[0].Title.Should().Be("Another Brick in the Wall");
    }

    [Fact]
    public void BuildQuery_BitrateGreaterThan_ReturnsMatchingSongs()
    {

        var playlist = CreatePlaylist(new SmartPlaylistRule
        {
            Field = SmartPlaylistField.Bitrate,
            Operator = SmartPlaylistOperator.GreaterThan,
            Value = "200"
        });

        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        results.Should().HaveCount(2);
        results.Should().OnlyContain(s => s.Bitrate > 200);
    }

    [Fact]
    public void BuildQuery_BitrateInRange_ReturnsMatchingSongs()
    {

        var playlist = CreatePlaylist(new SmartPlaylistRule
        {
            Field = SmartPlaylistField.Bitrate,
            Operator = SmartPlaylistOperator.IsInRange,
            Value = "190",
            SecondValue = "260"
        });

        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        results.Should().HaveCount(2);
        results.Should().OnlyContain(s => s.Bitrate >= 190 && s.Bitrate <= 260);
    }

    [Fact]
    public void BuildQuery_SampleRateEquals_ReturnsMatchingSongs()
    {

        var playlist = CreatePlaylist(new SmartPlaylistRule
        {
            Field = SmartPlaylistField.SampleRate,
            Operator = SmartPlaylistOperator.Equals,
            Value = "48000"
        });

        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        results.Should().HaveCount(1);
        results[0].Title.Should().Be("Another Brick in the Wall");
    }

    [Fact]
    public void BuildQuery_FileCreatedDateIsInTheLast_ReturnsRecentFiles()
    {

        var playlist = CreatePlaylist(new SmartPlaylistRule
        {
            Field = SmartPlaylistField.FileCreatedDate,
            Operator = SmartPlaylistOperator.IsInTheLast,
            Value = "250"
        });

        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        results.Should().HaveCount(1);
        results[0].Title.Should().Be("Another Brick in the Wall");
    }

    [Fact]
    public void BuildQuery_FileModifiedDateGreaterThan_ReturnsMatchingSongs()
    {

        var targetDate = DateTime.UtcNow.AddDays(-50).ToString("yyyy-MM-dd");
        var playlist = CreatePlaylist(new SmartPlaylistRule
        {
            Field = SmartPlaylistField.FileModifiedDate,
            Operator = SmartPlaylistOperator.GreaterThan,
            Value = targetDate
        });

        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        results.Should().HaveCount(1);
        results[0].Title.Should().Be("Come Together");
    }

    [Fact]
    public void BuildQuery_LastPlayedIsInTheLast_ExcludesNeverPlayedSongs()
    {

        var playlist = CreatePlaylist(new SmartPlaylistRule
        {
            Field = SmartPlaylistField.LastPlayed,
            Operator = SmartPlaylistOperator.IsInTheLast,
            Value = "365"
        });

        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        results.Should().HaveCount(3);
        results.Should().OnlyContain(s => s.LastPlayedDate != null);
    }

    [Fact]
    public void BuildQuery_MultipleGenresWithAndLogic_RequiresAllGenres()
    {

        var playlist = CreatePlaylist(
            matchAllRules: true,
            new SmartPlaylistRule
            {
                Field = SmartPlaylistField.Genre,
                Operator = SmartPlaylistOperator.Is,
                Value = "Rock"
            },
            new SmartPlaylistRule
            {
                Field = SmartPlaylistField.Genre,
                Operator = SmartPlaylistOperator.Is,
                Value = "Pop"
            });

        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        results.Should().HaveCount(1);
        results[0].Title.Should().Be("Something");
    }

    [Fact]
    public void BuildQuery_SortByArtistAsc_ReturnsSortedWithSecondarySort()
    {

        var playlist = new SmartPlaylist { SortOrder = SmartPlaylistSortOrder.ArtistAsc };

        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        results.Should().HaveCount(5);
        results[0].Title.Should().Be("Another Brick in the Wall");
        results[1].Title.Should().Be("Let It Be");
        results[2].Title.Should().Be("Come Together");
        results[3].Title.Should().Be("Something");
        results[4].Title.Should().Be("Fly Me to the Moon");
    }

    [Fact]
    public void BuildQuery_SortByAlbumAsc_GroupsByAlbumThenTrackNumber()
    {

        var playlist = new SmartPlaylist { SortOrder = SmartPlaylistSortOrder.AlbumAsc };

        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        results.Should().HaveCount(5);
        // Null albums (sorted by track number, nulls first)
        results[0].Album.Should().BeNull();
        results[1].Album.Should().BeNull();
        results[2].Album!.Title.Should().Be("Abbey Road");
        results[2].TrackNumber.Should().Be(1);
        results[3].Album!.Title.Should().Be("Abbey Road");
        results[3].TrackNumber.Should().Be(2);
        results[4].Album!.Title.Should().Be("The Wall");
    }

    [Fact]
    public void BuildQuery_SortByDateAddedDesc_ReturnsNewestFirst()
    {

        var playlist = new SmartPlaylist { SortOrder = SmartPlaylistSortOrder.DateAddedDesc };

        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        results.Should().HaveCount(5);
        results[0].Title.Should().Be("Let It Be");
        results[1].Title.Should().Be("Come Together");
        results[2].Title.Should().Be("Something");
        results[3].Title.Should().Be("Another Brick in the Wall");
        results[4].Title.Should().Be("Fly Me to the Moon");
    }

    [Fact]
    public void BuildQuery_SortByLastPlayedDesc_HandlesNullDates()
    {

        var playlist = new SmartPlaylist { SortOrder = SmartPlaylistSortOrder.LastPlayedDesc };

        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        results.Should().HaveCount(5);
        results[0].Title.Should().Be("Come Together");
        results[1].Title.Should().Be("Something");
        results[2].Title.Should().Be("Another Brick in the Wall");
        results[3].LastPlayedDate.Should().BeNull();
        results[4].LastPlayedDate.Should().BeNull();
    }

    [Fact]
    public void BuildQuery_SortByPlayCountAsc_ReturnsLeastPlayedFirst()
    {

        var playlist = new SmartPlaylist { SortOrder = SmartPlaylistSortOrder.PlayCountAsc };

        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        results.Should().HaveCount(5);
        results[0].PlayCount.Should().Be(0);
        results[1].PlayCount.Should().Be(25);
        results[2].PlayCount.Should().Be(50);
        results[3].PlayCount.Should().Be(100);
        results[4].PlayCount.Should().Be(200);
    }

    [Fact]
    public void BuildQuery_PlayCountNotEquals_ExcludesSongsWithExactCount()
    {
        var playlist = CreatePlaylist(new SmartPlaylistRule
        {
            Field = SmartPlaylistField.PlayCount,
            Operator = SmartPlaylistOperator.NotEquals,
            Value = "100"
        });

        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        results.Should().HaveCount(4, "only _song1 has PlayCount = 100 and should be excluded");
        results.Should().NotContain(s => s.PlayCount == 100);
    }

    [Fact]
    public void BuildQuery_ArtistStartsWith_MatchesByArtistNamePrefix()
    {
        var playlist = CreatePlaylist(new SmartPlaylistRule
        {
            Field = SmartPlaylistField.Artist,
            Operator = SmartPlaylistOperator.StartsWith,
            Value = "Pink"
        });

        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        results.Should().ContainSingle();
        results[0].Title.Should().Be("Another Brick in the Wall");
    }

    [Fact]
    public void BuildQuery_ArtistEndsWith_MatchesByArtistNameSuffix()
    {
        var playlist = CreatePlaylist(new SmartPlaylistRule
        {
            Field = SmartPlaylistField.Artist,
            Operator = SmartPlaylistOperator.EndsWith,
            Value = "Floyd"
        });

        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        results.Should().ContainSingle();
        results[0].Title.Should().Be("Another Brick in the Wall");
    }

    [Fact]
    public void BuildQuery_WithSortOrderOverride_UsesMappedSortOrderInsteadOfPlaylistDefault()
    {
        var playlist = new SmartPlaylist { SortOrder = SmartPlaylistSortOrder.TitleAsc };

        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist, sortOrderOverride: SongSortOrder.TitleDesc).ToList();

        results.Should().HaveCount(5);
        results[0].Title.Should().Be("Something");
        results[4].Title.Should().Be("Another Brick in the Wall");
    }

    [Fact]
    public void BuildQuery_WithTrackNumberSortOrderOverride_SortsSameAlbumNameAcrossDifferentArtists()
    {
        var compilationArtist1 = new Artist { Name = "Compilation Artist 1" };
        var compilationArtist2 = new Artist { Name = "Compilation Artist 2" };
        var compilationAlbum1 = new Album { Title = "Compilation" };
        compilationAlbum1.AlbumArtists.Add(new AlbumArtist { Artist = compilationArtist1, Order = 0 });
        var compilationAlbum2 = new Album { Title = "Compilation" };
        compilationAlbum2.AlbumArtists.Add(new AlbumArtist { Artist = compilationArtist2, Order = 0 });

        var track2 = new Song
        {
            Title = "Compilation Track 2",
            Album = compilationAlbum1,
            FolderId = _folder.Id,
            FilePath = "C:\\Music\\compilation-track-2.mp3",
            TrackNumber = 2,
            DiscNumber = 1
        };
        track2.SongArtists.Add(new SongArtist { Artist = compilationArtist1, Order = 0 });

        var track1 = new Song
        {
            Title = "Compilation Track 1",
            Album = compilationAlbum2,
            FolderId = _folder.Id,
            FilePath = "C:\\Music\\compilation-track-1.mp3",
            TrackNumber = 1,
            DiscNumber = 1
        };
        track1.SongArtists.Add(new SongArtist { Artist = compilationArtist2, Order = 0 });

        compilationAlbum1.SyncDenormalizedFields();
        compilationAlbum2.SyncDenormalizedFields();
        track1.SyncDenormalizedFields();
        track2.SyncDenormalizedFields();

        using (var seedContext = _dbHelper.ContextFactory.CreateDbContext())
        {
            seedContext.Songs.AddRange(track2, track1);
            seedContext.SaveChanges();
        }

        var playlist = new SmartPlaylist { SortOrder = SmartPlaylistSortOrder.TitleDesc };
        playlist.Rules.Add(new SmartPlaylistRule
        {
            Field = SmartPlaylistField.Album,
            Operator = SmartPlaylistOperator.Is,
            Value = "Compilation"
        });

        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist, sortOrderOverride: SongSortOrder.TrackNumberAsc).ToList();

        results.Select(s => s.Title).Should().ContainInOrder("Compilation Track 1", "Compilation Track 2");
    }

    [Fact]
    public void BuildCountQuery_WithSearchTerm_FiltersCount()
    {
        var playlist = CreatePlaylist(new SmartPlaylistRule
        {
            Field = SmartPlaylistField.Genre,
            Operator = SmartPlaylistOperator.Is,
            Value = "Rock"
        });

        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var count = _queryBuilder.BuildCountQuery(context, playlist, searchTerm: "Together").Count();

        count.Should().Be(1, "only 'Come Together' matches both the Rock genre rule and the search term");
    }

    [Fact]
    public void BuildQuery_SortByDurationAsc_ReturnsShorterSongsFirst()
    {
        var playlist = new SmartPlaylist { SortOrder = SmartPlaylistSortOrder.DurationAsc };

        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        results.Should().HaveCount(5);
        results[0].Title.Should().Be("Fly Me to the Moon");
        results[4].Title.Should().Be("Another Brick in the Wall");
    }

    [Fact]
    public void BuildQuery_SortByYearAsc_ReturnsOldestFirst()
    {
        var playlist = new SmartPlaylist { SortOrder = SmartPlaylistSortOrder.YearAsc };

        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        results.Should().HaveCount(5);
        results[0].Year.Should().Be(1964, "Fly Me to the Moon (1964) is the oldest");
        results.Last().Year.Should().Be(1979, "Another Brick in the Wall (1979) is the newest");
    }

    [Fact]
    public void BuildQuery_SortByDurationDesc_ReturnsLongestFirst()
    {
        var playlist = new SmartPlaylist { SortOrder = SmartPlaylistSortOrder.DurationDesc };

        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        results[0].Title.Should().Be("Another Brick in the Wall", "6.5 min is the longest");
        results[0].DurationTicks.Should().BeGreaterThan(results[1].DurationTicks);
    }

    [Fact]
    public void BuildQuery_SortByAlbumDesc_PutsHighestAlbumTitleFirst()
    {
        var playlist = new SmartPlaylist { SortOrder = SmartPlaylistSortOrder.AlbumDesc };

        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        results[0].Title.Should().Be("Another Brick in the Wall", "The Wall > Abbey Road descending");
    }

    [Fact]
    public void BuildQuery_SortByYearDesc_ReturnsNewestFirst()
    {
        var playlist = new SmartPlaylist { SortOrder = SmartPlaylistSortOrder.YearDesc };

        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        results[0].Year.Should().Be(1979, "Another Brick in the Wall (1979) is newest");
        results.Last().Year.Should().Be(1964, "Fly Me to the Moon (1964) is oldest");
    }

    [Fact]
    public void BuildQuery_SortByTrackNumberAsc_OrdersByAlbumThenDiscThenTrack()
    {
        var playlist = new SmartPlaylist { SortOrder = SmartPlaylistSortOrder.TrackNumberAsc };

        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        var abbeyRoadSongs = results.Where(s => s.Album?.Title == "Abbey Road").ToList();
        abbeyRoadSongs[0].TrackNumber.Should().Be(1, "track 1 comes before track 2 in Abbey Road");
        abbeyRoadSongs[1].TrackNumber.Should().Be(2);

        var wallIndex = results.FindIndex(s => s.Title == "Another Brick in the Wall");
        var abbeyRoadFirstIndex = results.FindIndex(s => s.Album?.Title == "Abbey Road");
        wallIndex.Should().BeGreaterThan(abbeyRoadFirstIndex, "'The Wall' should come after 'Abbey Road'");
    }

    [Fact]
    public void BuildQuery_SortByLastPlayedAsc_PutsNullLastPlayedFirst()
    {
        // Null LastPlayedDate sorts before non-null in SQLite ascending order
        var playlist = new SmartPlaylist { SortOrder = SmartPlaylistSortOrder.LastPlayedAsc };

        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        results.Last().Title.Should().Be("Come Together", "played most recently (-1 day) comes last in ascending");
    }

    [Fact]
    public void BuildQuery_DurationEquals_MatchesExactDuration()
    {
        var playlist = CreatePlaylist(new SmartPlaylistRule
        {
            Field = SmartPlaylistField.Duration,
            Operator = SmartPlaylistOperator.Equals,
            Value = "180"
        });

        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        results.Should().ContainSingle(s => s.Title == "Something");
    }

    [Fact]
    public void BuildQuery_DurationLessThan_ReturnsShorterSongs()
    {
        var playlist = CreatePlaylist(new SmartPlaylistRule
        {
            Field = SmartPlaylistField.Duration,
            Operator = SmartPlaylistOperator.LessThan,
            Value = "180"
        });

        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        results.Should().ContainSingle(s => s.Title == "Fly Me to the Moon");
    }

    [Fact]
    public void BuildQuery_BpmIsInRange_ReturnsMatchingSongs()
    {
        var playlist = CreatePlaylist(new SmartPlaylistRule
        {
            Field = SmartPlaylistField.Bpm,
            Operator = SmartPlaylistOperator.IsInRange,
            Value = "65",
            SecondValue = "90"
        });

        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        results.Should().HaveCount(2);
        results.Should().Contain(s => s.Title == "Come Together");
        results.Should().Contain(s => s.Title == "Something");
    }

    [Fact]
    public void BuildQuery_DateAddedLessThan_ReturnsOlderSongs()
    {
        var threshold = DateTime.UtcNow.AddDays(-20).ToString("o");
        var playlist = CreatePlaylist(new SmartPlaylistRule
        {
            Field = SmartPlaylistField.DateAdded,
            Operator = SmartPlaylistOperator.LessThan,
            Value = threshold
        });

        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        results.Should().HaveCount(3);
        results.Should().NotContain(s => s.Title == "Come Together");
        results.Should().NotContain(s => s.Title == "Let It Be");
    }

    [Theory]
    [InlineData(SmartPlaylistField.Grouping, SmartPlaylistOperator.Is, "Favorites")]
    [InlineData(SmartPlaylistField.Comment, SmartPlaylistOperator.Contains, "classic")]
    [InlineData(SmartPlaylistField.TrackNumber, SmartPlaylistOperator.Equals, "1")]
    [InlineData(SmartPlaylistField.Composer, SmartPlaylistOperator.Contains, "Lennon")]
    public void BuildQuery_TextAndTrackFields_SelectMatchingSong(
        SmartPlaylistField field, SmartPlaylistOperator ruleOperator, string value)
    {
        var playlist = CreatePlaylist(new SmartPlaylistRule { Field = field, Operator = ruleOperator, Value = value });
        using var context = _dbHelper.ContextFactory.CreateDbContext();

        _queryBuilder.BuildQuery(context, playlist).Select(song => song.Title).ToList()
            .Should().Equal("Come Together");
    }

    private static SmartPlaylist CreatePlaylist(params SmartPlaylistRule[] rules) => CreatePlaylist(true, rules);

    private static SmartPlaylist CreatePlaylist(bool matchAllRules, params SmartPlaylistRule[] rules) =>
        new() { Name = "Test Playlist", MatchAllRules = matchAllRules, Rules = rules };
}
