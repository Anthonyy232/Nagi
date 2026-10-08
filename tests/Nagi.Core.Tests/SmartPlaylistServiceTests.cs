using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Nagi.Core.Models;
using Nagi.Core.Services.Implementations;
using Nagi.Core.Tests.Utils;
using NSubstitute;
using Xunit;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;
using Nagi.Core.Helpers;

namespace Nagi.Core.Tests;

public class SmartPlaylistServiceTests : IDisposable
{
    private readonly DbContextFactoryTestHelper _dbHelper;
    private readonly ILogger<SmartPlaylistService> _logger;
    private readonly IFileSystemService _fileSystem;
    private readonly IPathConfiguration _pathConfig;
    private readonly IImageProcessor _imageProcessor;
    private readonly SmartPlaylistService _service;

    public SmartPlaylistServiceTests()
    {
        _dbHelper = new DbContextFactoryTestHelper();
        _logger = Substitute.For<ILogger<SmartPlaylistService>>();
        _fileSystem = Substitute.For<IFileSystemService>();
        _pathConfig = Substitute.For<IPathConfiguration>();
        _imageProcessor = Substitute.For<IImageProcessor>();

        _pathConfig.PlaylistImageCachePath.Returns("C:\\cache\\playlists");
        _fileSystem.Combine(Arg.Any<string>(), Arg.Any<string>())
            .Returns(callInfo => Path.Combine(callInfo.ArgAt<string[]>(0)));

        _service = new SmartPlaylistService(_dbHelper.ContextFactory, _fileSystem, _pathConfig, _imageProcessor, _logger);

        SeedTestData();
    }

    public void Dispose()
    {
        _dbHelper.Dispose();
    }

    private void SeedTestData()
    {
        using var context = _dbHelper.ContextFactory.CreateDbContext();

        var folder = new Folder { Name = "Music", Path = "C:\\Music" };
        var artist = new Artist { Name = "Test Artist" };
        var genreRock = new Genre { Name = "Rock" };
        var genrePop = new Genre { Name = "Pop" };

        var song1 = new Song
        {
            Title = "Song One",
            SongArtists = new List<SongArtist> { new SongArtist { Artist = artist, Order = 0 } },
            Folder = folder,
            FilePath = "C:\\Music\\song1.mp3",
            PlayCount = 100,
            IsLoved = true
        };
        song1.Genres.Add(genreRock);

        var song2 = new Song
        {
            Title = "Song Two",
            SongArtists = new List<SongArtist> { new SongArtist { Artist = artist, Order = 0 } },
            Folder = folder,
            FilePath = "C:\\Music\\song2.mp3",
            PlayCount = 50,
            IsLoved = false
        };
        song2.Genres.Add(genrePop);

        var song3 = new Song
        {
            Title = "Song Three",
            SongArtists = new List<SongArtist> { new SongArtist { Artist = artist, Order = 0 } },
            Folder = folder,
            FilePath = "C:\\Music\\song3.mp3",
            PlayCount = 200,
            IsLoved = true
        };
        song3.Genres.Add(genreRock);

        song1.SyncDenormalizedFields();
        song2.SyncDenormalizedFields();
        song3.SyncDenormalizedFields();

        context.Genres.AddRange(genreRock, genrePop);
        context.Songs.AddRange(song1, song2, song3);
        context.SaveChanges();
    }

    [Fact]
    public async Task CreateSmartPlaylistAsync_WithValidName_CreatesPlaylist()
    {
        var result = await _service.CreateSmartPlaylistAsync(new SmartPlaylist { Name = "My Smart Playlist", Description = "Test description" });

        result.Should().NotBeNull();
        result!.Name.Should().Be("My Smart Playlist");
        result.Description.Should().Be("Test description");
        result.MatchAllRules.Should().BeTrue();
        result.SortOrder.Should().Be(SmartPlaylistSortOrder.TitleAsc);
    }

    [Theory]
    [InlineData("My Playlist")]
    [InlineData("MY PLAYLIST")]
    public async Task CreateSmartPlaylistAsync_DuplicateName_UsesUniqueConstraint(string name)
    {
        await _service.CreateSmartPlaylistAsync(new SmartPlaylist { Name = "My Playlist" });
        var save = () => _service.CreateSmartPlaylistAsync(new SmartPlaylist { Name = name });

        var exception = await save.Should().ThrowAsync<DbUpdateException>();
        exception.Which.InnerException.Should().BeOfType<Microsoft.Data.Sqlite.SqliteException>()
            .Which.SqliteExtendedErrorCode.Should().Be(2067);
        (await _service.GetAllSmartPlaylistsAsync()).Should().ContainSingle();
    }

    [Fact]
    public async Task CreateSmartPlaylistAsync_TrimsWhitespace()
    {
        var result = await _service.CreateSmartPlaylistAsync(new SmartPlaylist { Name = "  Trimmed Name  " });

        result.Should().NotBeNull();
        result!.Name.Should().Be("Trimmed Name");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CreateSmartPlaylistAsync_WithBlankName_ThrowsArgumentException(string name)
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _service.CreateSmartPlaylistAsync(new SmartPlaylist { Name = name }));
    }

    [Fact]
    public async Task DeleteSmartPlaylistAsync_ExistingPlaylist_ReturnsTrue()
    {
        var playlist = await _service.CreateSmartPlaylistAsync(new SmartPlaylist { Name = "To Delete" });

        var result = await _service.DeleteSmartPlaylistAsync(playlist!.Id);

        result.Should().BeTrue();

        var fetched = await _service.GetSmartPlaylistByIdAsync(playlist.Id);
        fetched.Should().BeNull();
    }

    [Fact]
    public async Task DeleteSmartPlaylistAsync_NonExistingPlaylist_ReturnsFalse()
    {
        var result = await _service.DeleteSmartPlaylistAsync(Guid.NewGuid());

        result.Should().BeFalse();
    }

    [Fact]
    public async Task DeleteSmartPlaylistAsync_AlsoDeletesRules()
    {
        var playlist = await _service.CreateSmartPlaylistAsync(new SmartPlaylist
        {
            Name = "With Rules",
            Rules =
            [
                new SmartPlaylistRule { Field = SmartPlaylistField.Title, Operator = SmartPlaylistOperator.Contains, Value = "test" },
                new SmartPlaylistRule { Field = SmartPlaylistField.PlayCount, Operator = SmartPlaylistOperator.GreaterThan, Value = "10" }
            ]
        });

        await _service.DeleteSmartPlaylistAsync(playlist.Id);

        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var rules = await context.SmartPlaylistRules
            .Where(r => r.SmartPlaylistId == playlist.Id)
            .ToListAsync();
        rules.Should().BeEmpty();
    }

    [Fact]
    public async Task GetSmartPlaylistByIdAsync_ExistingPlaylist_ReturnsPlaylist()
    {
        var created = await _service.CreateSmartPlaylistAsync(new SmartPlaylist { Name = "Fetch Test" });

        var result = await _service.GetSmartPlaylistByIdAsync(created!.Id);

        result.Should().NotBeNull();
        result!.Name.Should().Be("Fetch Test");
    }

    [Fact]
    public async Task GetSmartPlaylistByIdAsync_NonExistingPlaylist_ReturnsNull()
    {
        var result = await _service.GetSmartPlaylistByIdAsync(Guid.NewGuid());

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetSmartPlaylistByIdAsync_IncludesRulesOrderedByOrder()
    {
        var playlist = await _service.CreateSmartPlaylistAsync(new SmartPlaylist
        {
            Name = "With Ordered Rules",
            Rules =
            [
                new SmartPlaylistRule { Field = SmartPlaylistField.Title, Operator = SmartPlaylistOperator.Contains, Value = "a" },
                new SmartPlaylistRule { Field = SmartPlaylistField.Artist, Operator = SmartPlaylistOperator.Is, Value = "b" }
            ]
        });

        var result = await _service.GetSmartPlaylistByIdAsync(playlist.Id);

        result!.Rules.Should().HaveCount(2);
        result.Rules.First().Field.Should().Be(SmartPlaylistField.Title);
        result.Rules.Last().Field.Should().Be(SmartPlaylistField.Artist);
    }

    [Fact]
    public async Task GetAllSmartPlaylistsAsync_ReturnsAllPlaylistsOrderedByName()
    {
        await _service.CreateSmartPlaylistAsync(new SmartPlaylist { Name = "Zebra" });
        await _service.CreateSmartPlaylistAsync(new SmartPlaylist { Name = "Alpha" });
        await _service.CreateSmartPlaylistAsync(new SmartPlaylist { Name = "Middle" });

        var results = (await _service.GetAllSmartPlaylistsAsync()).ToList();

        results.Should().HaveCount(3);
        results[0].Name.Should().Be("Alpha");
        results[1].Name.Should().Be("Middle");
        results[2].Name.Should().Be("Zebra");
    }

    [Fact]
    public async Task RenameSmartPlaylistAsync_ValidNewName_ReturnsTrue()
    {
        var playlist = await _service.CreateSmartPlaylistAsync(new SmartPlaylist { Name = "Original Name" });
        PlaylistUpdatedEventArgs? update = null;
        _service.PlaylistUpdated += (_, args) => update = args;
        var result = await _service.RenameSmartPlaylistAsync(playlist!.Id, "New Name");

        result.Should().BeTrue();

        var fetched = await _service.GetSmartPlaylistByIdAsync(playlist.Id);
        fetched!.Name.Should().Be("New Name");
        update.Should().NotBeNull();
        update!.DateModified.Should().Be(fetched.DateModified);
    }

    [Fact]
    public async Task RenameSmartPlaylistAsync_DuplicateName_ReturnsFalse()
    {
        await _service.CreateSmartPlaylistAsync(new SmartPlaylist { Name = "Existing Name" });
        var toRename = await _service.CreateSmartPlaylistAsync(new SmartPlaylist { Name = "To Rename" });

        var result = await _service.RenameSmartPlaylistAsync(toRename!.Id, "Existing Name");

        result.Should().BeFalse();
    }

    [Fact]
    public async Task RenameSmartPlaylistAsync_SameNameDifferentCase_ReturnsFalse()
    {
        await _service.CreateSmartPlaylistAsync(new SmartPlaylist { Name = "My Playlist" });
        var toRename = await _service.CreateSmartPlaylistAsync(new SmartPlaylist { Name = "Another Playlist" });

        var result = await _service.RenameSmartPlaylistAsync(toRename!.Id, "MY PLAYLIST");

        result.Should().BeFalse();
    }

    [Fact]
    public async Task RenameSmartPlaylistAsync_NonExistingPlaylist_ReturnsFalse()
    {
        var result = await _service.RenameSmartPlaylistAsync(Guid.NewGuid(), "New Name");

        result.Should().BeFalse();
    }

    [Fact]
    public async Task RenameSmartPlaylistAsync_EmptyName_ThrowsArgumentException()
    {
        var playlist = await _service.CreateSmartPlaylistAsync(new SmartPlaylist { Name = "Test" });

        await Assert.ThrowsAsync<ArgumentException>(() =>
            _service.RenameSmartPlaylistAsync(playlist!.Id, ""));
    }

    [Fact]
    public async Task UpdateSmartPlaylistCoverAsync_ValidPlaylist_UpdatesCover()
    {
        var playlist = await _service.CreateSmartPlaylistAsync(new SmartPlaylist { Name = "Cover Test" });
        const string localPath = "C:\\cover.jpg";
        _fileSystem.ReadAllBytesAsync(localPath).Returns(new byte[] { 1, 2, 3 });
        _imageProcessor.ProcessImageBytesAsync(Arg.Any<byte[]>()).Returns(new byte[] { 4, 5, 6 });
        _fileSystem.FileExists(localPath).Returns(true);
        _fileSystem.DirectoryExists(Arg.Any<string>()).Returns(true);
        _fileSystem.GetExtension(localPath).Returns(".jpg");

        var expectedPath = Path.Combine("C:\\cache\\playlists", $"{playlist!.Id}.custom.jpg");
        _fileSystem.FileExists(expectedPath).Returns(true);

        PlaylistUpdatedEventArgs? update = null;
        _service.PlaylistUpdated += (_, args) => update = args;
        var result = await _service.UpdateSmartPlaylistCoverAsync(playlist.Id, localPath);

        result.Should().BeTrue();

        var fetched = await _service.GetSmartPlaylistByIdAsync(playlist.Id);
        fetched!.CoverImageUri.Should().Be(expectedPath);
        update.Should().NotBeNull();
        update!.CoverImageUri.Should().Be(expectedPath);
        update.DateModified.Should().Be(fetched.DateModified);
        _fileSystem.Received(1).MoveFile(Arg.Is<string>(path => path.EndsWith(".tmp")), expectedPath, true);
    }

    [Fact]
    public async Task UpdateSmartPlaylistCoverAsync_NonExistingPlaylist_ReturnsFalse()
    {
        var result = await _service.UpdateSmartPlaylistCoverAsync(Guid.NewGuid(), "C:\\cover.jpg");

        result.Should().BeFalse();
    }

    [Fact]
    public async Task SetSortOrderAsync_UpdatesSortOrder()
    {
        var playlist = await _service.CreateSmartPlaylistAsync(new SmartPlaylist { Name = "Sort Test" });

        var result = await _service.SetSortOrderAsync(playlist!.Id, SmartPlaylistSortOrder.PlayCountDesc);

        result.Should().BeTrue();

        var fetched = await _service.GetSmartPlaylistByIdAsync(playlist.Id);
        fetched!.SortOrder.Should().Be(SmartPlaylistSortOrder.PlayCountDesc);
    }

    [Fact]
    public async Task GetMatchingSongsPagedAsync_ReturnsMatchingSongs()
    {
        var playlist = await _service.CreateSmartPlaylistAsync(new SmartPlaylist
        {
            Name = "Query Test",
            Rules =
            [
                new SmartPlaylistRule { Field = SmartPlaylistField.IsLoved, Operator = SmartPlaylistOperator.IsTrue, Value = null }
            ]
        });

        var songs = (await _service.GetMatchingSongsPagedAsync(playlist.Id, 1, 50)).Items;

        songs.Should().HaveCount(2);
        songs.Should().OnlyContain(s => s.IsLoved);
    }

    [Fact]
    public async Task GetMatchingSongsPagedAsync_WithSearchTerm_FiltersResults()
    {
        var playlist = await _service.CreateSmartPlaylistAsync(new SmartPlaylist { Name = "Search Test" });

        var songs = (await _service.GetMatchingSongsPagedAsync(playlist!.Id, 1, 50, "One")).Items;

        songs.Should().ContainSingle().Which.Title.Should().Be("Song One");
    }

    [Theory]
    [InlineData(1, "Song One", "Song Three")]
    [InlineData(2, "Song Two")]
    public async Task GetMatchingSongsPagedAsync_ReturnsExpectedPage(int page, params string[] expectedTitles)
    {
        var playlist = await _service.CreateSmartPlaylistAsync(new SmartPlaylist { Name = "Paged Test" });

        var result = await _service.GetMatchingSongsPagedAsync(playlist!.Id, page, 2);

        result.Items.Select(song => song.Title).Should().Equal(expectedTitles);
        result.TotalCount.Should().Be(3);
        result.PageNumber.Should().Be(page);
        result.PageSize.Should().Be(2);
        result.TotalPages.Should().Be(2);
    }

    [Fact]
    public async Task GetMatchingSongsPagedAsync_InvalidPaging_SanitizesToValidValues()
    {
        var playlist = await _service.CreateSmartPlaylistAsync(new SmartPlaylist { Name = "Invalid Page Test" });

        var result = await _service.GetMatchingSongsPagedAsync(playlist!.Id, 0, -5);

        result.PageNumber.Should().Be(1);
        result.PageSize.Should().Be(50);
    }

    [Fact]
    public async Task GetMatchingSongsPagedAsync_NonExistingPlaylist_ReturnsEmptyResult()
    {
        var result = await _service.GetMatchingSongsPagedAsync(Guid.NewGuid(), 1, 10);

        result.Items.Should().BeEmpty();
        result.TotalCount.Should().Be(0);
    }

    [Fact]
    public async Task GetMatchingSongCountAsync_ById_ReturnsCorrectCount()
    {
        var playlist = await _service.CreateSmartPlaylistAsync(new SmartPlaylist
        {
            Name = "Count Test",
            Rules =
            [
                new SmartPlaylistRule { Field = SmartPlaylistField.PlayCount, Operator = SmartPlaylistOperator.GreaterThan, Value = "75" }
            ]
        });

        var cancelledCount = () => _service.GetMatchingSongCountAsync(playlist.Id, token: new CancellationToken(true));
        await cancelledCount.Should().ThrowAsync<OperationCanceledException>();

        var count = await _service.GetMatchingSongCountAsync(playlist.Id);

        count.Should().Be(2);
    }

    [Fact]
    public async Task GetMatchingSongCountAsync_NonExistingPlaylist_ReturnsZero()
    {
        var count = await _service.GetMatchingSongCountAsync(Guid.NewGuid());

        count.Should().Be(0);
    }

    [Fact]
    public async Task GetMatchingSongCountAsync_ByObject_ReturnsCorrectCount()
    {
        var playlist = new SmartPlaylist
        {
            Name = "Object Test",
            Rules =
            {
                new SmartPlaylistRule
                {
                    Field = SmartPlaylistField.Genre,
                    Operator = SmartPlaylistOperator.Is,
                    Value = "Rock"
                }
            }
        };

        var cancelledCount = () => _service.GetMatchingSongCountAsync(playlist, token: new CancellationToken(true));
        await cancelledCount.Should().ThrowAsync<OperationCanceledException>();

        var count = await _service.GetMatchingSongCountAsync(playlist);

        count.Should().Be(2);
    }

    [Fact]
    public async Task GetMatchingSongIdsAsync_ReturnsMatchingIds()
    {
        var playlist = await _service.CreateSmartPlaylistAsync(new SmartPlaylist
        {
            Name = "IDs Test",
            Rules =
            [
                new SmartPlaylistRule { Field = SmartPlaylistField.Genre, Operator = SmartPlaylistOperator.Is, Value = "Pop" }
            ]
        });

        var ids = await _service.GetMatchingSongIdsAsync(playlist.Id);

        ids.Should().HaveCount(1);
    }

    [Fact]
    public async Task GetMatchingSongIdsAsync_WithSearchTerm_FiltersMatchingIds()
    {
        var playlist = await _service.CreateSmartPlaylistAsync(new SmartPlaylist
        {
            Name = "Search IDs Test",
            Rules =
            [
                new SmartPlaylistRule { Field = SmartPlaylistField.Genre, Operator = SmartPlaylistOperator.Is, Value = "Rock" }
            ]
        });

        var ids = await _service.GetMatchingSongIdsAsync(playlist.Id, "Three");

        ids.Should().HaveCount(1);

        await using var context = _dbHelper.ContextFactory.CreateDbContext();
        var expectedId = await context.Songs
            .Where(s => s.Title == "Song Three")
            .Select(s => s.Id)
            .SingleAsync();
        ids.Single().Should().Be(expectedId);
    }

    [Fact]
    public async Task GetMatchingSongIdsAsync_NonExistingPlaylist_ReturnsEmptyList()
    {
        var ids = await _service.GetMatchingSongIdsAsync(Guid.NewGuid());

        ids.Should().BeEmpty();
    }

    [Fact]
    public async Task GetAllMatchingSongCountsAsync_ReturnsCountsForAllPlaylists()
    {
        var playlist1 = await _service.CreateSmartPlaylistAsync(new SmartPlaylist
        {
            Name = "All Count 1",
            Rules =
            [
                new SmartPlaylistRule { Field = SmartPlaylistField.IsLoved, Operator = SmartPlaylistOperator.IsTrue, Value = null }
            ]
        });

        var playlist2 = await _service.CreateSmartPlaylistAsync(new SmartPlaylist
        {
            Name = "All Count 2",
            Rules =
            [
                new SmartPlaylistRule { Field = SmartPlaylistField.Genre, Operator = SmartPlaylistOperator.Is, Value = "Pop" }
            ]
        });

        var counts = await _service.GetAllMatchingSongCountsAsync();

        counts.Should().HaveCount(2);
        counts[playlist1.Id].Should().Be(2);
        counts[playlist2.Id].Should().Be(1);
    }

    [Fact]
    public async Task GetAllMatchingSongCountsAsync_NoPlaylists_ReturnsEmptyDictionary()
    {
        var counts = await _service.GetAllMatchingSongCountsAsync();

        counts.Should().BeEmpty();
    }

    [Fact]
    public async Task UpdateSmartPlaylistAsync_ValidPlaylist_UpdatesProperties()
    {
        var playlist = await _service.CreateSmartPlaylistAsync(new SmartPlaylist { Name = "Update Test" });
        playlist!.Name = "Updated Name";
        playlist.Description = "Updated Description";
        playlist.MatchAllRules = false;
        playlist.SortOrder = SmartPlaylistSortOrder.Random;

        PlaylistUpdatedEventArgs? update = null;
        _service.PlaylistUpdated += (_, args) => update = args;
        var result = await _service.UpdateSmartPlaylistAsync(playlist);

        result.Should().NotBeNull();

        var fetched = await _service.GetSmartPlaylistByIdAsync(playlist.Id);
        fetched!.Name.Should().Be("Updated Name");
        fetched.Description.Should().Be("Updated Description");
        fetched.MatchAllRules.Should().BeFalse();
        fetched.SortOrder.Should().Be(SmartPlaylistSortOrder.Random);
        update.Should().NotBeNull();
        update!.DateModified.Should().Be(fetched.DateModified);
    }

    [Fact]
    public async Task UpdateSmartPlaylistAsync_NonExistingPlaylist_ReturnsNull()
    {
        var playlist = new SmartPlaylist { Id = Guid.NewGuid(), Name = "Non-existent" };

        var result = await _service.UpdateSmartPlaylistAsync(playlist);

        result.Should().BeNull();
    }

    [Fact]
    public async Task CreateSmartPlaylistAsync_NotifiesAfterSavingCompleteDraft()
    {
        var draft = new SmartPlaylist
        {
            Name = "Configured playlist",
            MatchAllRules = false,
            SortOrder = SmartPlaylistSortOrder.PlayCountDesc,
            Rules =
            [
                new SmartPlaylistRule { Field = SmartPlaylistField.Genre, Operator = SmartPlaylistOperator.Is, Value = " Rock " },
                new SmartPlaylistRule { Field = SmartPlaylistField.PlayCount, Operator = SmartPlaylistOperator.GreaterThan, Value = "150" }
            ]
        };
        var originalRuleId = draft.Rules.First().Id;
        var notifications = 0;
        _service.PlaylistsChanged += (_, _) =>
        {
            notifications++;
            using var context = _dbHelper.ContextFactory.CreateDbContext();
            var stored = context.SmartPlaylists.Include(p => p.Rules).Single();
            stored.MatchAllRules.Should().BeFalse();
            stored.SortOrder.Should().Be(SmartPlaylistSortOrder.PlayCountDesc);
            stored.Rules.Should().HaveCount(2);
        };

        var saved = await _service.CreateSmartPlaylistAsync(draft);

        notifications.Should().Be(1);
        saved.Rules.Select(rule => rule.Order).Should().Equal(0, 1);
        saved.Rules.First().Value.Should().Be("Rock");
        draft.Rules.First().Id.Should().Be(originalRuleId);
        draft.Rules.First().Value.Should().Be(" Rock ");
        (await _service.GetMatchingSongCountAsync(saved.Id)).Should().Be(2);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SaveSmartPlaylistAsync_RuleInsertFails_RollsBackAndDoesNotNotify(bool isNew)
    {
        var draft = new SmartPlaylist
        {
            Name = "Original",
            Rules = [new SmartPlaylistRule { Field = SmartPlaylistField.Title, Operator = SmartPlaylistOperator.Contains, Value = "One" }]
        };
        if (!isNew)
            draft = await _service.CreateSmartPlaylistAsync(draft);
        draft.Name = "Changed";
        draft.MatchAllRules = false;
        draft.Rules.Single().Value = "Two";

        using (var context = _dbHelper.ContextFactory.CreateDbContext())
            await context.Database.ExecuteSqlRawAsync("""
                CREATE TRIGGER fail_rule_insert BEFORE INSERT ON SmartPlaylistRules
                BEGIN SELECT RAISE(ABORT, 'Test rule failure'); END;
                """);
        var notifications = 0;
        _service.PlaylistsChanged += (_, _) => notifications++;
        _service.PlaylistUpdated += (_, _) => notifications++;
        Func<Task> save = async () =>
        {
            if (isNew) await _service.CreateSmartPlaylistAsync(draft);
            else await _service.UpdateSmartPlaylistAsync(draft);
        };

        await save.Should().ThrowAsync<DbUpdateException>();

        notifications.Should().Be(0);
        var playlists = await _service.GetAllSmartPlaylistsAsync();
        if (isNew)
            playlists.Should().BeEmpty();
        else
        {
            var stored = playlists.Should().ContainSingle().Subject;
            stored.Name.Should().Be("Original");
            stored.MatchAllRules.Should().BeTrue();
            stored.Rules.Should().ContainSingle().Which.Value.Should().Be("One");
        }
    }

    [Fact]
    public async Task UpdateSmartPlaylistAsync_ReplacesRulesAndReturnsSavedState()
    {
        var draft = await _service.CreateSmartPlaylistAsync(new SmartPlaylist
        {
            Name = "Replace rules",
            Rules = [new SmartPlaylistRule { Field = SmartPlaylistField.IsLoved, Operator = SmartPlaylistOperator.IsTrue }]
        });
        var oldRuleId = draft.Rules.Single().Id;
        draft.Rules = [new SmartPlaylistRule { Field = SmartPlaylistField.Genre, Operator = SmartPlaylistOperator.Is, Value = "Pop" }];

        var saved = await _service.UpdateSmartPlaylistAsync(draft);

        saved!.Id.Should().Be(draft.Id);
        saved.DateCreated.Should().Be(draft.DateCreated);
        saved.Rules.Should().ContainSingle().Which.Value.Should().Be("Pop");
        using var context = _dbHelper.ContextFactory.CreateDbContext();
        (await context.SmartPlaylistRules.AnyAsync(rule => rule.Id == oldRuleId)).Should().BeFalse();
        (await _service.GetMatchingSongCountAsync(saved.Id)).Should().Be(1);
    }

    [Fact]
    public async Task SaveSmartPlaylistAsync_LocalizedRules_RemainStableAfterCultureChange()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            var saved = await _service.CreateSmartPlaylistAsync(new SmartPlaylist
            {
                Name = "Localized rules",
                Rules =
                [
                    new SmartPlaylistRule { Field = SmartPlaylistField.Duration, Operator = SmartPlaylistOperator.LessThan, Value = "180,5" },
                    new SmartPlaylistRule { Field = SmartPlaylistField.DateAdded, Operator = SmartPlaylistOperator.GreaterThan, Value = "01.02.2020" }
                ]
            });

            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            var reloaded = (await _service.GetSmartPlaylistByIdAsync(saved.Id))!;
            reloaded.Rules.First().Value.Should().Be("180.5");
            SmartPlaylistRuleValue.TryParseDate(reloaded.Rules.Last().Value, out var date).Should().BeTrue();
            date.Should().Be(new DateTime(2020, 2, 1));
            (await _service.GetMatchingSongCountAsync(saved.Id)).Should().Be(3);

            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            var display = SmartPlaylistRuleValue.ToDisplayValue(SmartPlaylistField.Duration, reloaded.Rules.First().Value);
            display.Should().Be("180,5");
            SmartPlaylistRuleValue.ToStoredValue(SmartPlaylistField.Duration, SmartPlaylistOperator.LessThan, display)
                .Should().Be("180.5");
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Fact]
    public async Task UpdateSmartPlaylistCoverAsync_UnreadableFile_KeepsExistingCover()
    {
        var playlist = await _service.CreateSmartPlaylistAsync(new SmartPlaylist { Name = "Cover failure" });
        const string existingPath = @"C:\cache\playlists\existing.jpg";
        using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            var stored = await context.SmartPlaylists.FindAsync(playlist.Id);
            stored!.CoverImageUri = existingPath;
            await context.SaveChangesAsync();
        }
        const string missingPath = @"C:\missing.jpg";
        _fileSystem.ReadAllBytesAsync(missingPath).Returns(Task.FromException<byte[]>(new FileNotFoundException()));
        var update = () => _service.UpdateSmartPlaylistCoverAsync(playlist.Id, missingPath);

        await update.Should().ThrowAsync<FileNotFoundException>();

        (await _service.GetSmartPlaylistByIdAsync(playlist.Id))!.CoverImageUri.Should().Be(existingPath);
        _fileSystem.DidNotReceive().DeleteFile(Arg.Any<string>());
    }

    [Fact]
    public async Task RenameSmartPlaylistAsync_ToSameName_Succeeds()
    {

        var playlist = await _service.CreateSmartPlaylistAsync(new SmartPlaylist { Name = "Same Name" });

        var result = await _service.RenameSmartPlaylistAsync(playlist!.Id, "Same Name");

        result.Should().BeTrue();
    }

    [Fact]
    public async Task GetMatchingSongsPagedAsync_VeryLargePageSize_IsCapped()
    {
        var playlist = await _service.CreateSmartPlaylistAsync(new SmartPlaylist { Name = "Large Page Test" });

        var result = await _service.GetMatchingSongsPagedAsync(playlist!.Id, 1, 10000);

        result.PageSize.Should().Be(500);
    }

    [Fact]
    public async Task GetMatchingSongsPagedAsync_PageBeyondData_ReturnsEmptyItems()
    {
        var playlist = await _service.CreateSmartPlaylistAsync(new SmartPlaylist { Name = "Beyond Page Test" });

        var result = await _service.GetMatchingSongsPagedAsync(playlist!.Id, 100, 10);

        result.Items.Should().BeEmpty();
        result.TotalCount.Should().Be(3);
    }

    [Fact]
    public async Task UpdateSmartPlaylistCoverAsync_ClearCover_SetsToNull()
    {
        var playlist = await _service.CreateSmartPlaylistAsync(new SmartPlaylist { Name = "Clear Cover Test" });
        using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            var stored = await context.SmartPlaylists.FindAsync(playlist!.Id);
            stored!.CoverImageUri = "C:\\cache\\playlists\\cover.jpg";
            await context.SaveChangesAsync();
        }

        var result = await _service.UpdateSmartPlaylistCoverAsync(playlist.Id, null);

        result.Should().BeTrue();

        var fetched = await _service.GetSmartPlaylistByIdAsync(playlist.Id);
        fetched!.CoverImageUri.Should().BeNull();
    }

    [Fact]
    public async Task SetSortOrderAsync_NonExistingPlaylist_ReturnsFalse()
    {
        var result = await _service.SetSortOrderAsync(Guid.NewGuid(), SmartPlaylistSortOrder.Random);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task GetMatchingSongCountAsync_WithSearchTerm_FiltersCount()
    {
        var playlist = await _service.CreateSmartPlaylistAsync(new SmartPlaylist
        {
            Name = "Search Count Test",
            Rules =
            [
                new SmartPlaylistRule { Field = SmartPlaylistField.Genre, Operator = SmartPlaylistOperator.Is, Value = "Rock" }
            ]
        });

        var countWithSearch = await _service.GetMatchingSongCountAsync(playlist.Id, "One");
        var countWithoutSearch = await _service.GetMatchingSongCountAsync(playlist.Id);

        countWithSearch.Should().Be(1);
        countWithoutSearch.Should().Be(2);
    }

}
