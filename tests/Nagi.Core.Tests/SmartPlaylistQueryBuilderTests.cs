using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Nagi.Core.Models;
using Nagi.Core.Services.Implementations;
using Nagi.Core.Tests.Utils;
using Xunit;

namespace Nagi.Core.Tests;

/// <summary>
///     Tests for the <see cref="SmartPlaylistQueryBuilder" /> to verify that rule predicates
///     are correctly built and applied to query songs. Tests focus on verifying the results
///     of applying rules rather than implementation details.
/// </summary>
public class SmartPlaylistQueryBuilderTests : IDisposable
{
    private readonly DbContextFactoryTestHelper _dbHelper;
    private readonly SmartPlaylistQueryBuilder _queryBuilder;

    // Test data
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

        // Create test data with a variety of values to test different predicates
        _artist1 = new Artist { Name = "The Beatles" };
        _artist2 = new Artist { Name = "Pink Floyd" };
        _album1 = new Album { Title = "Abbey Road", Artist = _artist1 };
        _album2 = new Album { Title = "The Wall", Artist = _artist2 };
        _folder = new Folder { Name = "Music", Path = "C:\\Music" };
        _genreRock = new Genre { Name = "Rock" };
        _genrePop = new Genre { Name = "Pop" };
        _genreJazz = new Genre { Name = "Jazz" };

        _song1 = new Song
        {
            Title = "Come Together",
            Artist = _artist1,
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
        _song1.Genres.Add(_genreRock);

        _song2 = new Song
        {
            Title = "Something",
            Artist = _artist1,
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
        _song2.Genres.Add(_genreRock);
        _song2.Genres.Add(_genrePop);

        _song3 = new Song
        {
            Title = "Another Brick in the Wall",
            Artist = _artist2,
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
        _song3.Genres.Add(_genreRock);

        _song4 = new Song
        {
            Title = "Let It Be",
            Artist = _artist1,
            Album = null, // No album
            Folder = _folder,
            FilePath = "C:\\Music\\letitbe.mp3",
            PlayCount = 0,
            SkipCount = 0,
            Rating = null, // No rating
            Year = 1970,
            IsLoved = false,
            Duration = TimeSpan.FromMinutes(4),
            DateAddedToLibrary = DateTime.UtcNow.AddDays(-5)
        };
        _song4.Genres.Add(_genrePop);

        _song5 = new Song
        {
            Title = "Fly Me to the Moon",
            Artist = null, // No artist
            Album = null, // No album
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

        // Seed the database
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

    #region Text Predicate Tests

    [Fact]
    public void BuildQuery_TitleIs_ReturnsExactMatch()
    {
        // Arrange
        var playlist = CreatePlaylist(new SmartPlaylistRule
        {
            Field = SmartPlaylistField.Title,
            Operator = SmartPlaylistOperator.Is,
            Value = "Come Together"
        });

        // Act
        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        // Assert
        results.Should().HaveCount(1);
        results[0].Title.Should().Be("Come Together");
    }

    [Fact]
    public void BuildQuery_TitleIs_IsCaseInsensitive()
    {
        // Arrange
        var playlist = CreatePlaylist(new SmartPlaylistRule
        {
            Field = SmartPlaylistField.Title,
            Operator = SmartPlaylistOperator.Is,
            Value = "come together" // lowercase
        });

        // Act
        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        // Assert
        results.Should().HaveCount(1);
        results[0].Title.Should().Be("Come Together");
    }

    [Fact]
    public void BuildQuery_TitleIsNot_ExcludesMatch()
    {
        // Arrange
        var playlist = CreatePlaylist(new SmartPlaylistRule
        {
            Field = SmartPlaylistField.Title,
            Operator = SmartPlaylistOperator.IsNot,
            Value = "Come Together"
        });

        // Act
        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        // Assert
        results.Should().HaveCount(4);
        results.Should().NotContain(s => s.Title == "Come Together");
    }

    [Fact]
    public void BuildQuery_TitleContains_ReturnsPartialMatches()
    {
        // Arrange
        var playlist = CreatePlaylist(new SmartPlaylistRule
        {
            Field = SmartPlaylistField.Title,
            Operator = SmartPlaylistOperator.Contains,
            Value = "the"
        });

        // Act
        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        // Assert - "Come Together", "Another Brick in the Wall", "Fly Me to the Moon"
        results.Should().HaveCount(3);
        results.Should().Contain(s => s.Title == "Come Together");
        results.Should().Contain(s => s.Title == "Another Brick in the Wall");
        results.Should().Contain(s => s.Title == "Fly Me to the Moon");
    }

    [Fact]
    public void BuildQuery_TitleDoesNotContain_ExcludesPartialMatches()
    {
        // Arrange
        var playlist = CreatePlaylist(new SmartPlaylistRule
        {
            Field = SmartPlaylistField.Title,
            Operator = SmartPlaylistOperator.DoesNotContain,
            Value = "the"
        });

        // Act
        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        // Assert - "Something", "Let It Be"
        results.Should().HaveCount(2);
        results.Should().Contain(s => s.Title == "Something");
        results.Should().Contain(s => s.Title == "Let It Be");
    }

    [Fact]
    public void BuildQuery_TitleStartsWith_ReturnsMatchingPrefix()
    {
        // Arrange
        var playlist = CreatePlaylist(new SmartPlaylistRule
        {
            Field = SmartPlaylistField.Title,
            Operator = SmartPlaylistOperator.StartsWith,
            Value = "Let"
        });

        // Act
        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        // Assert
        results.Should().HaveCount(1);
        results[0].Title.Should().Be("Let It Be");
    }

    [Fact]
    public void BuildQuery_TitleEndsWith_ReturnsMatchingSuffix()
    {
        // Arrange
        var playlist = CreatePlaylist(new SmartPlaylistRule
        {
            Field = SmartPlaylistField.Title,
            Operator = SmartPlaylistOperator.EndsWith,
            Value = "Moon"
        });

        // Act
        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        // Assert
        results.Should().HaveCount(1);
        results[0].Title.Should().Be("Fly Me to the Moon");
    }

    [Fact]
    public void BuildQuery_ArtistIs_ReturnsMatchingArtist()
    {
        // Arrange
        var playlist = CreatePlaylist(new SmartPlaylistRule
        {
            Field = SmartPlaylistField.Artist,
            Operator = SmartPlaylistOperator.Is,
            Value = "The Beatles"
        });

        // Act
        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        // Assert
        results.Should().HaveCount(3); // _song1, _song2, _song4
        results.Should().OnlyContain(s => s.Artist!.Name == "The Beatles");
    }

    [Fact]
    public void BuildQuery_ArtistIsNot_IncludesSongsWithNullArtist()
    {
        // Arrange
        var playlist = CreatePlaylist(new SmartPlaylistRule
        {
            Field = SmartPlaylistField.Artist,
            Operator = SmartPlaylistOperator.IsNot,
            Value = "The Beatles"
        });

        // Act
        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        // Assert - Pink Floyd song + null artist song
        results.Should().HaveCount(2);
        results.Should().Contain(s => s.Title == "Another Brick in the Wall");
        results.Should().Contain(s => s.Title == "Fly Me to the Moon");
    }

    [Fact]
    public void BuildQuery_AlbumContains_ReturnsMatchingAlbums()
    {
        // Arrange
        var playlist = CreatePlaylist(new SmartPlaylistRule
        {
            Field = SmartPlaylistField.Album,
            Operator = SmartPlaylistOperator.Contains,
            Value = "Road"
        });

        // Act
        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        // Assert - Abbey Road songs
        results.Should().HaveCount(2);
        results.Should().OnlyContain(s => s.Album!.Title == "Abbey Road");
    }

    #endregion

    #region Genre Predicate Tests

    [Fact]
    public void BuildQuery_GenreIs_ReturnsMatchingGenre()
    {
        // Arrange
        var playlist = CreatePlaylist(new SmartPlaylistRule
        {
            Field = SmartPlaylistField.Genre,
            Operator = SmartPlaylistOperator.Is,
            Value = "Rock"
        });

        // Act
        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        // Assert - _song1, _song2, _song3 all have Rock genre
        results.Should().HaveCount(3);
        results.Should().OnlyContain(s => s.Genres.Any(g => g.Name == "Rock"));
    }

    [Fact]
    public void BuildQuery_GenreIsNot_ExcludesGenre()
    {
        // Arrange
        var playlist = CreatePlaylist(new SmartPlaylistRule
        {
            Field = SmartPlaylistField.Genre,
            Operator = SmartPlaylistOperator.IsNot,
            Value = "Rock"
        });

        // Act
        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        // Assert - _song4 (Pop only), _song5 (Jazz only)
        results.Should().HaveCount(2);
        results.Should().NotContain(s => s.Genres.Any(g => g.Name == "Rock"));
    }

    [Fact]
    public void BuildQuery_GenreContains_ReturnsPartialMatch()
    {
        // Arrange
        var playlist = CreatePlaylist(new SmartPlaylistRule
        {
            Field = SmartPlaylistField.Genre,
            Operator = SmartPlaylistOperator.Contains,
            Value = "az" // partial match for "Jazz"
        });

        // Act
        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        // Assert
        results.Should().HaveCount(1);
        results[0].Title.Should().Be("Fly Me to the Moon");
    }

    [Fact]
    public void BuildQuery_GenreIs_IsCaseInsensitive()
    {
        // Arrange
        var playlist = CreatePlaylist(new SmartPlaylistRule
        {
            Field = SmartPlaylistField.Genre,
            Operator = SmartPlaylistOperator.Is,
            Value = "rock" // lowercase
        });

        // Act
        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        // Assert
        results.Should().HaveCount(3);
    }

    #endregion

    #region Numeric Predicate Tests

    [Fact]
    public void BuildQuery_PlayCountEquals_ReturnsExactMatch()
    {
        // Arrange
        var playlist = CreatePlaylist(new SmartPlaylistRule
        {
            Field = SmartPlaylistField.PlayCount,
            Operator = SmartPlaylistOperator.Equals,
            Value = "100"
        });

        // Act
        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        // Assert
        results.Should().HaveCount(1);
        results[0].PlayCount.Should().Be(100);
    }

    [Fact]
    public void BuildQuery_PlayCountGreaterThan_ReturnsMatchingSongs()
    {
        // Arrange
        var playlist = CreatePlaylist(new SmartPlaylistRule
        {
            Field = SmartPlaylistField.PlayCount,
            Operator = SmartPlaylistOperator.GreaterThan,
            Value = "50"
        });

        // Act
        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        // Assert - Songs with PlayCount > 50: _song1 (100), _song3 (200)
        results.Should().HaveCount(2);
        results.Should().OnlyContain(s => s.PlayCount > 50);
    }

    [Fact]
    public void BuildQuery_PlayCountLessThanOrEqual_ReturnsMatchingSongs()
    {
        // Arrange
        var playlist = CreatePlaylist(new SmartPlaylistRule
        {
            Field = SmartPlaylistField.PlayCount,
            Operator = SmartPlaylistOperator.LessThanOrEqual,
            Value = "25"
        });

        // Act
        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        // Assert - _song4 (0), _song5 (25)
        results.Should().HaveCount(2);
        results.Should().OnlyContain(s => s.PlayCount <= 25);
    }

    [Fact]
    public void BuildQuery_PlayCountInRange_ReturnsMatchingSongs()
    {
        // Arrange
        var playlist = CreatePlaylist(new SmartPlaylistRule
        {
            Field = SmartPlaylistField.PlayCount,
            Operator = SmartPlaylistOperator.IsInRange,
            Value = "40",
            SecondValue = "150"
        });

        // Act
        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        // Assert - _song1 (100), _song2 (50)
        results.Should().HaveCount(2);
        results.Should().OnlyContain(s => s.PlayCount >= 40 && s.PlayCount <= 150);
    }

    [Fact]
    public void BuildQuery_RatingEquals_ReturnsNullableMatch()
    {
        // Arrange
        var playlist = CreatePlaylist(new SmartPlaylistRule
        {
            Field = SmartPlaylistField.Rating,
            Operator = SmartPlaylistOperator.Equals,
            Value = "5"
        });

        // Act
        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        // Assert - _song1, _song3 both have Rating = 5
        results.Should().HaveCount(2);
        results.Should().OnlyContain(s => s.Rating == 5);
    }

    [Fact]
    public void BuildQuery_YearInRange_ReturnsMatchingSongs()
    {
        // Arrange
        var playlist = CreatePlaylist(new SmartPlaylistRule
        {
            Field = SmartPlaylistField.Year,
            Operator = SmartPlaylistOperator.IsInRange,
            Value = "1965",
            SecondValue = "1970"
        });

        // Act
        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        // Assert - 1969, 1969, 1970 (but not 1979 or 1964)
        results.Should().HaveCount(3);
        results.Should().OnlyContain(s => s.Year >= 1965 && s.Year <= 1970);
    }

    [Fact]
    public void BuildQuery_InvalidNumericValue_ReturnsNoFilter()
    {
        // Arrange
        var playlist = CreatePlaylist(new SmartPlaylistRule
        {
            Field = SmartPlaylistField.PlayCount,
            Operator = SmartPlaylistOperator.Equals,
            Value = "not-a-number"
        });

        // Act
        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        // Assert - Should return all songs when the rule can't be parsed
        results.Should().HaveCount(5);
    }

    #endregion

    #region Duration Predicate Tests

    [Fact]
    public void BuildQuery_DurationGreaterThan_ReturnsMatchingSongs()
    {
        // Arrange - Duration in seconds
        var playlist = CreatePlaylist(new SmartPlaylistRule
        {
            Field = SmartPlaylistField.Duration,
            Operator = SmartPlaylistOperator.GreaterThan,
            Value = "240" // 4 minutes in seconds
        });

        // Act
        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        // Assert - _song1 (4.5 min), _song3 (6.5 min)
        results.Should().HaveCount(2);
        results.Should().OnlyContain(s => s.Duration > TimeSpan.FromSeconds(240));
    }

    [Fact]
    public void BuildQuery_DurationInRange_ReturnsMatchingSongs()
    {
        // Arrange
        var playlist = CreatePlaylist(new SmartPlaylistRule
        {
            Field = SmartPlaylistField.Duration,
            Operator = SmartPlaylistOperator.IsInRange,
            Value = "180", // 3 min
            SecondValue = "300" // 5 min
        });

        // Act
        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        // Assert - _song1 (4.5 min), _song2 (3 min), _song4 (4 min)
        results.Should().HaveCount(3);
    }

    #endregion

    #region Boolean Predicate Tests

    [Fact]
    public void BuildQuery_IsLovedTrue_ReturnsLovedSongs()
    {
        // Arrange
        var playlist = CreatePlaylist(new SmartPlaylistRule
        {
            Field = SmartPlaylistField.IsLoved,
            Operator = SmartPlaylistOperator.IsTrue
        });

        // Act
        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        // Assert - _song1, _song2 are loved
        results.Should().HaveCount(2);
        results.Should().OnlyContain(s => s.IsLoved);
    }

    [Fact]
    public void BuildQuery_IsLovedFalse_ReturnsNotLovedSongs()
    {
        // Arrange
        var playlist = CreatePlaylist(new SmartPlaylistRule
        {
            Field = SmartPlaylistField.IsLoved,
            Operator = SmartPlaylistOperator.IsFalse
        });

        // Act
        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        // Assert
        results.Should().HaveCount(3);
        results.Should().OnlyContain(s => !s.IsLoved);
    }

    [Fact]
    public void BuildQuery_HasLyrics_ReturnsSongsWithLrcFile()
    {
        // Arrange
        var playlist = CreatePlaylist(new SmartPlaylistRule
        {
            Field = SmartPlaylistField.HasLyrics,
            Operator = SmartPlaylistOperator.IsTrue
        });

        // Act
        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        // Assert - Only _song3 has LrcFilePath
        results.Should().HaveCount(1);
        results[0].Title.Should().Be("Another Brick in the Wall");
    }

    #endregion

    #region Date Predicate Tests

    [Fact]
    public void BuildQuery_DateAddedInTheLast_ReturnsRecentSongs()
    {
        // Arrange
        var playlist = CreatePlaylist(new SmartPlaylistRule
        {
            Field = SmartPlaylistField.DateAdded,
            Operator = SmartPlaylistOperator.IsInTheLast,
            Value = "15" // last 15 days
        });

        // Act
        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        // Assert - _song1 (10 days), _song4 (5 days)
        results.Should().HaveCount(2);
    }

    [Fact]
    public void BuildQuery_DateAddedNotInTheLast_ReturnsOlderSongs()
    {
        // Arrange
        var playlist = CreatePlaylist(new SmartPlaylistRule
        {
            Field = SmartPlaylistField.DateAdded,
            Operator = SmartPlaylistOperator.IsNotInTheLast,
            Value = "15" // older than 15 days
        });

        // Act
        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        // Assert - _song2 (30 days), _song3 (60 days), _song5 (100 days)
        results.Should().HaveCount(3);
    }

    #endregion

    #region Sort Order Tests

    [Fact]
    public void BuildQuery_SortByTitleAsc_ReturnsSortedResults()
    {
        // Arrange
        var playlist = CreatePlaylist(sortOrder: SmartPlaylistSortOrder.TitleAsc);

        // Act
        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        // Assert
        results.Should().HaveCount(5);
        results[0].Title.Should().Be("Another Brick in the Wall");
        results[1].Title.Should().Be("Come Together");
        results[4].Title.Should().Be("Something");
    }

    [Fact]
    public void BuildQuery_SortByTitleDesc_ReturnsSortedResults()
    {
        // Arrange
        var playlist = CreatePlaylist(sortOrder: SmartPlaylistSortOrder.TitleDesc);

        // Act
        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        // Assert
        results[0].Title.Should().Be("Something");
        results[4].Title.Should().Be("Another Brick in the Wall");
    }

    [Fact]
    public void BuildQuery_SortByPlayCountDesc_ReturnsSortedResults()
    {
        // Arrange
        var playlist = CreatePlaylist(sortOrder: SmartPlaylistSortOrder.PlayCountDesc);

        // Act
        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        // Assert
        results[0].PlayCount.Should().Be(200);
        results[1].PlayCount.Should().Be(100);
    }

    #endregion

    #region Rule Logic Tests (AND/OR)

    [Fact]
    public void BuildQuery_MatchAllRulesTrue_AppliesAndLogic()
    {
        // Arrange - Songs that are both loved AND from The Beatles
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

        // Act
        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        // Assert - _song1 and _song2 are both loved and by The Beatles
        results.Should().HaveCount(2);
        results.Should().OnlyContain(s => s.IsLoved && s.Artist!.Name == "The Beatles");
    }

    [Fact]
    public void BuildQuery_MatchAllRulesFalse_AppliesOrLogic()
    {
        // Arrange - Songs that are either Jazz OR Year = 1979
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

        // Act
        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        // Assert - _song3 (1979), _song5 (Jazz)
        results.Should().HaveCount(2);
    }

    #endregion

    #region Edge Cases

    [Fact]
    public void BuildQuery_NoRules_ReturnsAllSongs()
    {
        // Arrange
        var playlist = CreatePlaylist();

        // Act
        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        // Assert
        results.Should().HaveCount(5);
    }

    [Fact]
    public void BuildQuery_EmptyValue_HandlesGracefully()
    {
        // Arrange
        var playlist = CreatePlaylist(new SmartPlaylistRule
        {
            Field = SmartPlaylistField.Title,
            Operator = SmartPlaylistOperator.Contains,
            Value = "" // Empty value
        });

        // Act
        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        // Assert - Empty string is contained in all titles
        results.Should().HaveCount(5);
    }

    [Fact]
    public void BuildQuery_NullValue_HandlesGracefully()
    {
        // Arrange
        var playlist = CreatePlaylist(new SmartPlaylistRule
        {
            Field = SmartPlaylistField.Title,
            Operator = SmartPlaylistOperator.Contains,
            Value = null
        });

        // Act
        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        // Assert - Null is treated as empty string, contained in all titles
        results.Should().HaveCount(5);
    }

    [Fact]
    public void BuildQuery_WithSearchTerm_AppliesAdditionalFilter()
    {
        // Arrange
        var playlist = CreatePlaylist(new SmartPlaylistRule
        {
            Field = SmartPlaylistField.Genre,
            Operator = SmartPlaylistOperator.Is,
            Value = "Rock"
        });

        // Act
        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist, searchTerm: "beatles").ToList();

        // Assert - Rock songs by The Beatles
        results.Should().HaveCount(2);
        results.Should().OnlyContain(s => s.Artist!.Name.Contains("Beatles", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildCountQuery_ReturnsCorrectCount()
    {
        // Arrange
        var playlist = CreatePlaylist(new SmartPlaylistRule
        {
            Field = SmartPlaylistField.PlayCount,
            Operator = SmartPlaylistOperator.GreaterThan,
            Value = "50"
        });

        // Act
        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var count = _queryBuilder.BuildCountQuery(context, playlist).Count();

        // Assert
        count.Should().Be(2);
    }

    [Fact]
    public void BuildQuery_ComposerContains_HandlesNullComposer()
    {
        // Arrange
        var playlist = CreatePlaylist(new SmartPlaylistRule
        {
            Field = SmartPlaylistField.Composer,
            Operator = SmartPlaylistOperator.Contains,
            Value = "Lennon"
        });

        // Act
        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        // Assert - Only _song1 has "Lennon" in Composer
        results.Should().HaveCount(1);
        results[0].Title.Should().Be("Come Together");
    }

    [Fact]
    public void BuildQuery_ComposerDoesNotContain_IncludesNullComposers()
    {
        // Arrange
        var playlist = CreatePlaylist(new SmartPlaylistRule
        {
            Field = SmartPlaylistField.Composer,
            Operator = SmartPlaylistOperator.DoesNotContain,
            Value = "Harrison"
        });

        // Act
        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        // Assert - All except _song2 (which has George Harrison as composer)
        results.Should().HaveCount(4);
        results.Should().NotContain(s => s.Composer != null && s.Composer.Contains("Harrison"));
    }

    #endregion

    #region Additional Field Tests

    [Fact]
    public void BuildQuery_SkipCountGreaterThan_ReturnsMatchingSongs()
    {
        // Arrange
        var playlist = CreatePlaylist(new SmartPlaylistRule
        {
            Field = SmartPlaylistField.SkipCount,
            Operator = SmartPlaylistOperator.GreaterThan,
            Value = "3"
        });

        // Act
        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        // Assert - _song1 (5), _song2 (10)
        results.Should().HaveCount(2);
        results.Should().OnlyContain(s => s.SkipCount > 3);
    }

    [Fact]
    public void BuildQuery_BpmGreaterThan_ReturnsMatchingSongsWithDouble()
    {
        // Arrange - BPM is a double field
        var playlist = CreatePlaylist(new SmartPlaylistRule
        {
            Field = SmartPlaylistField.Bpm,
            Operator = SmartPlaylistOperator.GreaterThan,
            Value = "70"
        });

        // Act
        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        // Assert - _song1 (82 bpm)
        results.Should().HaveCount(1);
        results[0].Title.Should().Be("Come Together");
    }

    [Fact]
    public void BuildQuery_BpmEquals_UsesApproximateComparison()
    {
        // Arrange - BPM uses approximate comparison (within 0.01)
        var playlist = CreatePlaylist(new SmartPlaylistRule
        {
            Field = SmartPlaylistField.Bpm,
            Operator = SmartPlaylistOperator.Equals,
            Value = "82"
        });

        // Act
        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        // Assert
        results.Should().HaveCount(1);
        results[0].Bpm.Should().Be(82);
    }

    [Fact]
    public void BuildQuery_LastPlayedIsNull_HandlesNeverPlayedSongs()
    {
        // Arrange - IsNotInTheLast should include songs that have never been played (null LastPlayed)
        var playlist = CreatePlaylist(new SmartPlaylistRule
        {
            Field = SmartPlaylistField.LastPlayed,
            Operator = SmartPlaylistOperator.IsNotInTheLast,
            Value = "5" // Older than 5 days or null
        });

        // Act
        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        // Assert - Songs older than 5 days OR never played (null): _song2 (7 days), _song3 (14 days), _song4 (never), _song5 (never)
        // _song1 (1 day ago) should be excluded as it was played within the last 5 days
        results.Should().HaveCount(4);
    }

    #endregion

    #region Advanced Edge Cases

    [Fact]
    public void BuildQuery_SongWithMultipleGenres_MatchesOnAnyGenre()
    {
        // Arrange - Song2 has both Rock and Pop
        var playlist = CreatePlaylist(new SmartPlaylistRule
        {
            Field = SmartPlaylistField.Genre,
            Operator = SmartPlaylistOperator.Is,
            Value = "Pop"
        });

        // Act
        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        // Assert - _song2 (Rock + Pop) and _song4 (Pop)
        results.Should().HaveCount(2);
        results.Should().Contain(s => s.Title == "Something"); // Has Rock AND Pop
        results.Should().Contain(s => s.Title == "Let It Be"); // Has Pop
    }

    [Fact]
    public void BuildQuery_RangeWithEqualMinMax_ReturnsSingleValue()
    {
        // Arrange - Range where min equals max
        var playlist = CreatePlaylist(new SmartPlaylistRule
        {
            Field = SmartPlaylistField.Year,
            Operator = SmartPlaylistOperator.IsInRange,
            Value = "1969",
            SecondValue = "1969"
        });

        // Act
        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        // Assert - Only 1969 songs: _song1, _song2
        results.Should().HaveCount(2);
        results.Should().OnlyContain(s => s.Year == 1969);
    }

    [Fact]
    public void BuildQuery_MixedValidAndInvalidRules_AppliesOnlyValidRules()
    {
        // Arrange - One valid rule, one with invalid value
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
                Value = "not-a-number" // Invalid - should be ignored
            });

        // Act
        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        // Assert - Should return Rock songs (invalid rule ignored): _song1, _song2, _song3
        results.Should().HaveCount(3);
    }

    [Fact]
    public void BuildQuery_SortByRandom_DoesNotThrow()
    {
        // Arrange
        var playlist = CreatePlaylist(sortOrder: SmartPlaylistSortOrder.Random);

        // Act
        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var act = () => _queryBuilder.BuildQuery(context, playlist).ToList();

        // Assert - Should not throw and return all songs
        act.Should().NotThrow();
        var results = act();
        results.Should().HaveCount(5);
    }

    [Fact]
    public void BuildQuery_UnsupportedOperatorForField_ReturnsNull()
    {
        // Arrange - Using text operator on boolean field (unsupported combination)
        var playlist = CreatePlaylist(new SmartPlaylistRule
        {
            Field = SmartPlaylistField.IsLoved,
            Operator = SmartPlaylistOperator.Contains, // Not valid for boolean
            Value = "true"
        });

        // Act
        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        // Assert - Invalid rule should be ignored, returning all songs
        results.Should().HaveCount(5);
    }

    [Fact]
    public void BuildQuery_DateGreaterThan_ReturnsMatchingSongs()
    {
        // Arrange - Date-based comparison
        var targetDate = DateTime.UtcNow.AddDays(-20).ToString("yyyy-MM-dd");
        var playlist = CreatePlaylist(new SmartPlaylistRule
        {
            Field = SmartPlaylistField.DateAdded,
            Operator = SmartPlaylistOperator.GreaterThan,
            Value = targetDate
        });

        // Act
        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        // Assert - Songs added in last 20 days: _song1 (10 days), _song4 (5 days)
        results.Should().HaveCount(2);
    }

    [Fact]
    public void BuildQuery_OrLogicWithMultipleGenreRules_MatchesEither()
    {
        // Arrange - Match songs that are either Jazz OR Pop
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

        // Act
        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        // Assert - _song2 (Pop+Rock), _song4 (Pop), _song5 (Jazz)
        results.Should().HaveCount(3);
    }

    [Fact]
    public void BuildQuery_AndLogicWithContradictoryRules_ReturnsEmpty()
    {
        // Arrange - Contradictory rules with AND logic
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

        // Act
        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        // Assert - No song can be both 1969 AND 1979
        results.Should().BeEmpty();
    }

    [Fact]
    public void BuildQuery_GroupingField_ReturnsMatchingSongs()
    {
        // Arrange
        var playlist = CreatePlaylist(new SmartPlaylistRule
        {
            Field = SmartPlaylistField.Grouping,
            Operator = SmartPlaylistOperator.Is,
            Value = "Favorites"
        });

        // Act
        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        // Assert - Only _song1 has Grouping = "Favorites"
        results.Should().HaveCount(1);
        results[0].Title.Should().Be("Come Together");
    }

    [Fact]
    public void BuildQuery_CommentField_ReturnsMatchingSongs()
    {
        // Arrange
        var playlist = CreatePlaylist(new SmartPlaylistRule
        {
            Field = SmartPlaylistField.Comment,
            Operator = SmartPlaylistOperator.Contains,
            Value = "classic"
        });

        // Act
        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        // Assert - Only _song1 has Comment containing "classic"
        results.Should().HaveCount(1);
        results[0].Title.Should().Be("Come Together");
    }

    [Fact]
    public void BuildQuery_EmptySearchTerm_DoesNotFilter()
    {
        // Arrange
        var playlist = CreatePlaylist(new SmartPlaylistRule
        {
            Field = SmartPlaylistField.Genre,
            Operator = SmartPlaylistOperator.Is,
            Value = "Rock"
        });

        // Act
        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var resultsNoSearch = _queryBuilder.BuildQuery(context, playlist, searchTerm: null).ToList();
        var resultsEmptySearch = _queryBuilder.BuildQuery(context, playlist, searchTerm: "").ToList();
        var resultsWhitespaceSearch = _queryBuilder.BuildQuery(context, playlist, searchTerm: "   ").ToList();

        // Assert - All should return same results (3 Rock songs)
        resultsNoSearch.Should().HaveCount(3);
        resultsEmptySearch.Should().HaveCount(3);
        resultsWhitespaceSearch.Should().HaveCount(3);
    }

    #endregion

    #region Missing Field Coverage Tests

    [Fact]
    public void BuildQuery_TrackNumberEquals_ReturnsMatchingSongs()
    {
        // Arrange
        var playlist = CreatePlaylist(new SmartPlaylistRule
        {
            Field = SmartPlaylistField.TrackNumber,
            Operator = SmartPlaylistOperator.Equals,
            Value = "1"
        });

        // Act
        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        // Assert - Only _song1 has TrackNumber = 1
        results.Should().HaveCount(1);
        results[0].Title.Should().Be("Come Together");
    }

    [Fact]
    public void BuildQuery_TrackNumberGreaterThan_ReturnsMatchingSongs()
    {
        // Arrange
        var playlist = CreatePlaylist(new SmartPlaylistRule
        {
            Field = SmartPlaylistField.TrackNumber,
            Operator = SmartPlaylistOperator.GreaterThan,
            Value = "1"
        });

        // Act
        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        // Assert - _song2 (2), _song3 (5), excludes nulls and track 1
        results.Should().HaveCount(2);
        results.Should().OnlyContain(s => s.TrackNumber > 1);
    }

    [Fact]
    public void BuildQuery_DiscNumberEquals_ReturnsMatchingSongs()
    {
        // Arrange
        var playlist = CreatePlaylist(new SmartPlaylistRule
        {
            Field = SmartPlaylistField.DiscNumber,
            Operator = SmartPlaylistOperator.Equals,
            Value = "2"
        });

        // Act
        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        // Assert - Only _song3 has DiscNumber = 2
        results.Should().HaveCount(1);
        results[0].Title.Should().Be("Another Brick in the Wall");
    }

    [Fact]
    public void BuildQuery_BitrateGreaterThan_ReturnsMatchingSongs()
    {
        // Arrange
        var playlist = CreatePlaylist(new SmartPlaylistRule
        {
            Field = SmartPlaylistField.Bitrate,
            Operator = SmartPlaylistOperator.GreaterThan,
            Value = "200"
        });

        // Act
        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        // Assert - _song1 (320), _song2 (256)
        results.Should().HaveCount(2);
        results.Should().OnlyContain(s => s.Bitrate > 200);
    }

    [Fact]
    public void BuildQuery_BitrateInRange_ReturnsMatchingSongs()
    {
        // Arrange
        var playlist = CreatePlaylist(new SmartPlaylistRule
        {
            Field = SmartPlaylistField.Bitrate,
            Operator = SmartPlaylistOperator.IsInRange,
            Value = "190",
            SecondValue = "260"
        });

        // Act
        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        // Assert - _song2 (256), _song3 (192)
        results.Should().HaveCount(2);
        results.Should().OnlyContain(s => s.Bitrate >= 190 && s.Bitrate <= 260);
    }

    [Fact]
    public void BuildQuery_SampleRateEquals_ReturnsMatchingSongs()
    {
        // Arrange
        var playlist = CreatePlaylist(new SmartPlaylistRule
        {
            Field = SmartPlaylistField.SampleRate,
            Operator = SmartPlaylistOperator.Equals,
            Value = "48000"
        });

        // Act
        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        // Assert - Only _song3 has SampleRate = 48000
        results.Should().HaveCount(1);
        results[0].Title.Should().Be("Another Brick in the Wall");
    }

    [Fact]
    public void BuildQuery_FileCreatedDateIsInTheLast_ReturnsRecentFiles()
    {
        // Arrange
        var playlist = CreatePlaylist(new SmartPlaylistRule
        {
            Field = SmartPlaylistField.FileCreatedDate,
            Operator = SmartPlaylistOperator.IsInTheLast,
            Value = "250" // Files created in the last 250 days
        });

        // Act
        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        // Assert - Only _song3 has FileCreatedDate within last 250 days (200 days ago)
        results.Should().HaveCount(1);
        results[0].Title.Should().Be("Another Brick in the Wall");
    }

    [Fact]
    public void BuildQuery_FileModifiedDateGreaterThan_ReturnsMatchingSongs()
    {
        // Arrange
        var targetDate = DateTime.UtcNow.AddDays(-50).ToString("yyyy-MM-dd");
        var playlist = CreatePlaylist(new SmartPlaylistRule
        {
            Field = SmartPlaylistField.FileModifiedDate,
            Operator = SmartPlaylistOperator.GreaterThan,
            Value = targetDate
        });

        // Act
        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        // Assert - _song1 (30 days ago)
        results.Should().HaveCount(1);
        results[0].Title.Should().Be("Come Together");
    }

    [Fact]
    public void BuildQuery_LastPlayedIsInTheLast_ExcludesNeverPlayedSongs()
    {
        // Arrange - IsInTheLast should ONLY include songs with a non-null LastPlayedDate
        var playlist = CreatePlaylist(new SmartPlaylistRule
        {
            Field = SmartPlaylistField.LastPlayed,
            Operator = SmartPlaylistOperator.IsInTheLast,
            Value = "365" // Played in the last year (all played songs should match)
        });

        // Act
        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        // Assert - _song1, _song2, _song3 have LastPlayedDate; _song4, _song5 are null
        results.Should().HaveCount(3);
        results.Should().OnlyContain(s => s.LastPlayedDate != null);
    }

    [Fact]
    public void BuildQuery_MultipleGenresWithAndLogic_RequiresAllGenres()
    {
        // Arrange - Song must have BOTH Rock AND Pop genres (AND logic with multiple genre rules)
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

        // Act
        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        // Assert - Only _song2 has both Rock AND Pop genres
        results.Should().HaveCount(1);
        results[0].Title.Should().Be("Something");
    }

    #endregion

    #region Sort Order Stability Tests

    [Fact]
    public void BuildQuery_SortByArtistAsc_ReturnsSortedWithSecondarySort()
    {
        // Arrange
        var playlist = CreatePlaylist(sortOrder: SmartPlaylistSortOrder.ArtistAsc);

        // Act
        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        // Assert - Null artist first (sorted as empty string), then Pink Floyd, then The Beatles
        // Within same artist, should be sorted by album, then track number, then title
        results.Should().HaveCount(5);
        results[0].Title.Should().Be("Fly Me to the Moon"); // No artist
        results[1].Title.Should().Be("Another Brick in the Wall"); // Pink Floyd
        // The Beatles songs sorted by album (null first), then track
        results[2].Title.Should().Be("Let It Be"); // No album
        results[3].Title.Should().Be("Come Together"); // Abbey Road, Track 1
        results[4].Title.Should().Be("Something"); // Abbey Road, Track 2
    }

    [Fact]
    public void BuildQuery_SortByAlbumAsc_GroupsByAlbumThenTrackNumber()
    {
        // Arrange
        var playlist = CreatePlaylist(sortOrder: SmartPlaylistSortOrder.AlbumAsc);

        // Act
        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        // Assert - Null albums first, then Abbey Road (track 1, 2), then The Wall (track 5)
        results.Should().HaveCount(5);
        // Null albums (sorted by track number, nulls first)
        results[0].Album.Should().BeNull();
        results[1].Album.Should().BeNull();
        // Abbey Road songs by track number
        results[2].Album!.Title.Should().Be("Abbey Road");
        results[2].TrackNumber.Should().Be(1);
        results[3].Album!.Title.Should().Be("Abbey Road");
        results[3].TrackNumber.Should().Be(2);
        // The Wall
        results[4].Album!.Title.Should().Be("The Wall");
    }

    [Fact]
    public void BuildQuery_SortByDateAddedDesc_ReturnsNewestFirst()
    {
        // Arrange
        var playlist = CreatePlaylist(sortOrder: SmartPlaylistSortOrder.DateAddedDesc);

        // Act
        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        // Assert - Most recently added first
        results.Should().HaveCount(5);
        results[0].Title.Should().Be("Let It Be"); // 5 days ago
        results[1].Title.Should().Be("Come Together"); // 10 days ago
        results[2].Title.Should().Be("Something"); // 30 days ago
        results[3].Title.Should().Be("Another Brick in the Wall"); // 60 days ago
        results[4].Title.Should().Be("Fly Me to the Moon"); // 100 days ago
    }

    [Fact]
    public void BuildQuery_SortByLastPlayedDesc_HandlesNullDates()
    {
        // Arrange
        var playlist = CreatePlaylist(sortOrder: SmartPlaylistSortOrder.LastPlayedDesc);

        // Act
        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        // Assert - Most recently played first, nulls last
        results.Should().HaveCount(5);
        results[0].Title.Should().Be("Come Together"); // 1 day ago
        results[1].Title.Should().Be("Something"); // 7 days ago
        results[2].Title.Should().Be("Another Brick in the Wall"); // 14 days ago
        // Null LastPlayedDates are last
        results[3].LastPlayedDate.Should().BeNull();
        results[4].LastPlayedDate.Should().BeNull();
    }

    [Fact]
    public void BuildQuery_SortByPlayCountAsc_ReturnsLeastPlayedFirst()
    {
        // Arrange
        var playlist = CreatePlaylist(sortOrder: SmartPlaylistSortOrder.PlayCountAsc);

        // Act
        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var results = _queryBuilder.BuildQuery(context, playlist).ToList();

        // Assert - Least played first
        results.Should().HaveCount(5);
        results[0].PlayCount.Should().Be(0); // _song4
        results[1].PlayCount.Should().Be(25); // _song5
        results[2].PlayCount.Should().Be(50); // _song2
        results[3].PlayCount.Should().Be(100); // _song1
        results[4].PlayCount.Should().Be(200); // _song3
    }

    #endregion

    #region Helper Methods


    private static SmartPlaylist CreatePlaylist(
        params SmartPlaylistRule[] rules)
    {
        return CreatePlaylist(true, SmartPlaylistSortOrder.TitleAsc, rules);
    }

    private static SmartPlaylist CreatePlaylist(
        bool matchAllRules,
        params SmartPlaylistRule[] rules)
    {
        return CreatePlaylist(matchAllRules, SmartPlaylistSortOrder.TitleAsc, rules);
    }

    private static SmartPlaylist CreatePlaylist(
        SmartPlaylistSortOrder sortOrder)
    {
        return CreatePlaylist(true, sortOrder, Array.Empty<SmartPlaylistRule>());
    }

    private static SmartPlaylist CreatePlaylist(
        bool matchAllRules = true,
        SmartPlaylistSortOrder sortOrder = SmartPlaylistSortOrder.TitleAsc,
        params SmartPlaylistRule[] rules)
    {
        var playlist = new SmartPlaylist
        {
            Name = "Test Playlist",
            MatchAllRules = matchAllRules,
            SortOrder = sortOrder
        };

        foreach (var rule in rules)
        {
            rule.Order = playlist.Rules.Count;
            playlist.Rules.Add(rule);
        }

        return playlist;
    }

    #endregion
}
