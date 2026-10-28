using FluentAssertions;
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
    }

    [Theory]
    [InlineData(SmartPlaylistField.Title, SmartPlaylistOperator.Is, "come together", null, "Come Together")]
    [InlineData(SmartPlaylistField.Title, SmartPlaylistOperator.IsNot, "Come Together", null,
        "Something", "Another Brick in the Wall", "Let It Be", "Fly Me to the Moon")]
    [InlineData(SmartPlaylistField.Title, SmartPlaylistOperator.Contains, "the", null,
        "Come Together", "Another Brick in the Wall", "Fly Me to the Moon")]
    [InlineData(SmartPlaylistField.Title, SmartPlaylistOperator.DoesNotContain, "the", null, "Something", "Let It Be")]
    [InlineData(SmartPlaylistField.Title, SmartPlaylistOperator.StartsWith, "Let", null, "Let It Be")]
    [InlineData(SmartPlaylistField.Title, SmartPlaylistOperator.EndsWith, "Moon", null, "Fly Me to the Moon")]
    [InlineData(SmartPlaylistField.Artist, SmartPlaylistOperator.Is, "The Beatles", null, "Come Together", "Something", "Let It Be")]
    [InlineData(SmartPlaylistField.Artist, SmartPlaylistOperator.IsNot, "The Beatles", null, "Another Brick in the Wall", "Fly Me to the Moon")]
    [InlineData(SmartPlaylistField.Album, SmartPlaylistOperator.Contains, "Road", null, "Come Together", "Something")]
    [InlineData(SmartPlaylistField.Genre, SmartPlaylistOperator.IsNot, "Rock", null, "Let It Be", "Fly Me to the Moon")]
    [InlineData(SmartPlaylistField.Genre, SmartPlaylistOperator.Contains, "az", null, "Fly Me to the Moon")]
    [InlineData(SmartPlaylistField.Genre, SmartPlaylistOperator.Is, "rock", null, "Come Together", "Something", "Another Brick in the Wall")]
    [InlineData(SmartPlaylistField.Genre, SmartPlaylistOperator.StartsWith, "rO", null, "Come Together", "Something", "Another Brick in the Wall")]
    [InlineData(SmartPlaylistField.Genre, SmartPlaylistOperator.EndsWith, "cK", null, "Come Together", "Something", "Another Brick in the Wall")]
    [InlineData(SmartPlaylistField.PlayCount, SmartPlaylistOperator.Equals, "100", null, "Come Together")]
    [InlineData(SmartPlaylistField.PlayCount, SmartPlaylistOperator.GreaterThan, "50", null, "Come Together", "Another Brick in the Wall")]
    [InlineData(SmartPlaylistField.PlayCount, SmartPlaylistOperator.LessThanOrEqual, "25", null, "Let It Be", "Fly Me to the Moon")]
    [InlineData(SmartPlaylistField.PlayCount, SmartPlaylistOperator.IsInRange, "40", "150", "Come Together", "Something")]
    [InlineData(SmartPlaylistField.Rating, SmartPlaylistOperator.Equals, "5", null, "Come Together", "Another Brick in the Wall")]
    [InlineData(SmartPlaylistField.Year, SmartPlaylistOperator.IsInRange, "1965", "1970", "Come Together", "Something", "Let It Be")]
    [InlineData(SmartPlaylistField.Duration, SmartPlaylistOperator.GreaterThan, "240", null, "Come Together", "Another Brick in the Wall")]
    [InlineData(SmartPlaylistField.Duration, SmartPlaylistOperator.IsInRange, "180", "300", "Come Together", "Something", "Let It Be")]
    [InlineData(SmartPlaylistField.Composer, SmartPlaylistOperator.DoesNotContain, "Harrison", null,
        "Come Together", "Another Brick in the Wall", "Let It Be", "Fly Me to the Moon")]
    [InlineData(SmartPlaylistField.SkipCount, SmartPlaylistOperator.GreaterThan, "3", null, "Come Together", "Something")]
    [InlineData(SmartPlaylistField.Bpm, SmartPlaylistOperator.GreaterThan, "70", null, "Come Together")]
    [InlineData(SmartPlaylistField.Bpm, SmartPlaylistOperator.Equals, "82", null, "Come Together")]
    [InlineData(SmartPlaylistField.Year, SmartPlaylistOperator.IsInRange, "1969", "1969", "Come Together", "Something")]
    [InlineData(SmartPlaylistField.TrackNumber, SmartPlaylistOperator.GreaterThan, "1", null, "Something", "Another Brick in the Wall")]
    [InlineData(SmartPlaylistField.DiscNumber, SmartPlaylistOperator.Equals, "2", null, "Another Brick in the Wall")]
    [InlineData(SmartPlaylistField.Bitrate, SmartPlaylistOperator.GreaterThan, "200", null, "Come Together", "Something")]
    [InlineData(SmartPlaylistField.Bitrate, SmartPlaylistOperator.IsInRange, "190", "260", "Something", "Another Brick in the Wall")]
    [InlineData(SmartPlaylistField.SampleRate, SmartPlaylistOperator.Equals, "48000", null, "Another Brick in the Wall")]
    [InlineData(SmartPlaylistField.PlayCount, SmartPlaylistOperator.NotEquals, "100", null,
        "Something", "Another Brick in the Wall", "Let It Be", "Fly Me to the Moon")]
    [InlineData(SmartPlaylistField.Artist, SmartPlaylistOperator.StartsWith, "Pink", null, "Another Brick in the Wall")]
    [InlineData(SmartPlaylistField.Artist, SmartPlaylistOperator.EndsWith, "Floyd", null, "Another Brick in the Wall")]
    [InlineData(SmartPlaylistField.Duration, SmartPlaylistOperator.Equals, "180", null, "Something")]
    [InlineData(SmartPlaylistField.Duration, SmartPlaylistOperator.LessThan, "180", null, "Fly Me to the Moon")]
    [InlineData(SmartPlaylistField.Bpm, SmartPlaylistOperator.IsInRange, "65", "90", "Come Together", "Something")]
    public void BuildQuery_RuleFilters_SelectExpectedSongs(
        SmartPlaylistField field, SmartPlaylistOperator ruleOperator, string value, string? secondValue,
        params string[] expectedTitles)
    {
        var playlist = CreatePlaylist(new SmartPlaylistRule
        {
            Field = field,
            Operator = ruleOperator,
            Value = value,
            SecondValue = secondValue
        });
        using var context = _dbHelper.ContextFactory.CreateDbContext();

        _queryBuilder.BuildQuery(context, playlist).Select(song => song.Title).ToList()
            .Should().BeEquivalentTo(expectedTitles);
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

            results.Should().ContainSingle().Which.Title.Should().Be("Collaboration");
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

            results.Should().ContainSingle().Which.Title.Should().Be("Album Track");
        }
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
                Field = SmartPlaylistField.Duration,
                Operator = SmartPlaylistOperator.LessThan,
                Value = value
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

        results.Should().ContainSingle().Which.Title.Should().Be("Another Brick in the Wall");
    }

    [Theory]
    [InlineData(SmartPlaylistSortOrder.TitleAsc, "Another Brick in the Wall", "Come Together", "Fly Me to the Moon", "Let It Be", "Something")]
    [InlineData(SmartPlaylistSortOrder.TitleDesc, "Something", "Let It Be", "Fly Me to the Moon", "Come Together", "Another Brick in the Wall")]
    [InlineData(SmartPlaylistSortOrder.PlayCountDesc, "Another Brick in the Wall", "Come Together",
        "Something", "Fly Me to the Moon", "Let It Be")]
    [InlineData(SmartPlaylistSortOrder.ArtistAsc, "Another Brick in the Wall", "Let It Be", "Come Together", "Something", "Fly Me to the Moon")]
    [InlineData(SmartPlaylistSortOrder.DateAddedDesc, "Let It Be", "Come Together",
        "Something", "Another Brick in the Wall", "Fly Me to the Moon")]
    [InlineData(SmartPlaylistSortOrder.PlayCountAsc, "Let It Be", "Fly Me to the Moon",
        "Something", "Come Together", "Another Brick in the Wall")]
    [InlineData(SmartPlaylistSortOrder.DurationAsc, "Fly Me to the Moon", "Something",
        "Let It Be", "Come Together", "Another Brick in the Wall")]
    [InlineData(SmartPlaylistSortOrder.YearAsc, "Fly Me to the Moon", "Come Together", "Something", "Let It Be", "Another Brick in the Wall")]
    [InlineData(SmartPlaylistSortOrder.DurationDesc, "Another Brick in the Wall", "Come Together",
        "Let It Be", "Something", "Fly Me to the Moon")]
    [InlineData(SmartPlaylistSortOrder.AlbumDesc, "Another Brick in the Wall", "Something", "Come Together", "Let It Be", "Fly Me to the Moon")]
    [InlineData(SmartPlaylistSortOrder.YearDesc, "Another Brick in the Wall", "Let It Be", "Something", "Come Together", "Fly Me to the Moon")]
    [InlineData(SmartPlaylistSortOrder.TrackNumberAsc, "Fly Me to the Moon", "Let It Be", "Come Together", "Something", "Another Brick in the Wall")]
    public void BuildQuery_SortOrder_ReturnsExpectedSequence(
        SmartPlaylistSortOrder sortOrder, params string[] expectedTitles)
    {
        using var context = _dbHelper.ContextFactory.CreateDbContext();

        _queryBuilder.BuildQuery(context, new SmartPlaylist { SortOrder = sortOrder })
            .Select(song => song.Title).ToList().Should().Equal(expectedTitles);
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

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void BuildQuery_EmptyContainsValue_MatchesAllTitles(string? value)
    {
        var playlist = CreatePlaylist(new SmartPlaylistRule
        {
            Field = SmartPlaylistField.Title,
            Operator = SmartPlaylistOperator.Contains,
            Value = value
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

        results.Select(song => song.Title).Should().BeEquivalentTo(
            "Something", "Another Brick in the Wall", "Let It Be", "Fly Me to the Moon");
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
    public void BuildQuery_EmptySearchTerm_DoesNotFilter()
    {
        var playlist = CreatePlaylist(new SmartPlaylistRule
        {
            Field = SmartPlaylistField.Genre,
            Operator = SmartPlaylistOperator.Is,
            Value = "Rock"
        });

        using var context = _dbHelper.ContextFactory.CreateDbContext();
        foreach (var searchTerm in new string?[] { null, "", "   " })
        {
            var titles = _queryBuilder.BuildQuery(context, playlist, searchTerm)
                .Select(song => song.Title).ToList();

            titles.Should().BeEquivalentTo("Come Together", "Something", "Another Brick in the Wall");
        }
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

        results.Should().ContainSingle().Which.Title.Should().Be("Another Brick in the Wall");
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

        results.Should().ContainSingle().Which.Title.Should().Be("Come Together");
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

        results.Select(song => song.Title).Should().BeEquivalentTo(
            "Come Together", "Something", "Another Brick in the Wall");
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

        results.Should().ContainSingle().Which.Title.Should().Be("Something");
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
    public void BuildQuery_SortByLastPlayedAsc_PutsNullLastPlayedFirst()
    {
        // Null LastPlayedDate sorts before non-null in SQLite ascending order
        var playlist = new SmartPlaylist { SortOrder = SmartPlaylistSortOrder.LastPlayedAsc };

        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        results.Last().Title.Should().Be("Come Together", "played most recently (-1 day) comes last in ascending");
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

    [Fact]
    public void BuildQuery_TextRules_FoldAccentedCapitals()
    {
        // The in-memory database is opened before EF can register Nagi's SQLite functions, so use a file.
        using var fileDb = new DbContextFactoryTestHelper(useFileDatabase: true);
        var artist = new Artist { Name = "Édith Piaf" };
        var song = new Song { Title = "Énorme", Folder = new Folder { Name = "Music", Path = "C:\\Music" }, FilePath = "C:\\Music\\a.mp3" };
        song.SongArtists.Add(new SongArtist { Artist = artist, Order = 0 });
        song.SyncDenormalizedFields();
        using (var seed = fileDb.ContextFactory.CreateDbContext())
        {
            seed.Songs.Add(song);
            seed.SaveChanges();
        }
        var playlist = CreatePlaylist(
            new SmartPlaylistRule { Field = SmartPlaylistField.Artist, Operator = SmartPlaylistOperator.Is, Value = "ÉDITH PIAF" },
            new SmartPlaylistRule { Field = SmartPlaylistField.Title, Operator = SmartPlaylistOperator.StartsWith, Value = "éNO" });
        using var context = fileDb.ContextFactory.CreateDbContext();

        _queryBuilder.BuildQuery(context, playlist).Select(s => s.Title).ToList().Should().Equal("Énorme");
    }

    private static SmartPlaylist CreatePlaylist(params SmartPlaylistRule[] rules) => CreatePlaylist(true, rules);

    private static SmartPlaylist CreatePlaylist(bool matchAllRules, params SmartPlaylistRule[] rules) =>
        new() { Name = "Test Playlist", MatchAllRules = matchAllRules, Rules = rules };
}
