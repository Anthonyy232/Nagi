using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nagi.Core.Models;
using Nagi.Core.Services.Implementations;
using Nagi.Core.Tests.Utils;
using NSubstitute;
using Xunit;

namespace Nagi.Core.Tests;

/// <summary>
///     Tests for the <see cref="SmartPlaylistService" /> to verify CRUD operations,
///     rule management, and query execution functionality.
/// </summary>
public class SmartPlaylistServiceTests : IDisposable
{
    private readonly DbContextFactoryTestHelper _dbHelper;
    private readonly ILogger<SmartPlaylistService> _logger;
    private readonly SmartPlaylistService _service;

    public SmartPlaylistServiceTests()
    {
        _dbHelper = new DbContextFactoryTestHelper();
        _logger = Substitute.For<ILogger<SmartPlaylistService>>();
        _service = new SmartPlaylistService(_dbHelper.ContextFactory, _logger);

        // Seed test data
        SeedTestData();
    }

    public void Dispose()
    {
        _dbHelper.Dispose();
        GC.SuppressFinalize(this);
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
            Artist = artist,
            Folder = folder,
            FilePath = "C:\\Music\\song1.mp3",
            PlayCount = 100,
            IsLoved = true
        };
        song1.Genres.Add(genreRock);

        var song2 = new Song
        {
            Title = "Song Two",
            Artist = artist,
            Folder = folder,
            FilePath = "C:\\Music\\song2.mp3",
            PlayCount = 50,
            IsLoved = false
        };
        song2.Genres.Add(genrePop);

        var song3 = new Song
        {
            Title = "Song Three",
            Artist = artist,
            Folder = folder,
            FilePath = "C:\\Music\\song3.mp3",
            PlayCount = 200,
            IsLoved = true
        };
        song3.Genres.Add(genreRock);

        context.Genres.AddRange(genreRock, genrePop);
        context.Songs.AddRange(song1, song2, song3);
        context.SaveChanges();
    }

    #region CRUD Operation Tests

    [Fact]
    public async Task CreateSmartPlaylistAsync_WithValidName_CreatesPlaylist()
    {
        // Act
        var result = await _service.CreateSmartPlaylistAsync("My Smart Playlist", "Test description");

        // Assert
        result.Should().NotBeNull();
        result!.Name.Should().Be("My Smart Playlist");
        result.Description.Should().Be("Test description");
        result.MatchAllRules.Should().BeTrue(); // Default value
        result.SortOrder.Should().Be(SmartPlaylistSortOrder.TitleAsc); // Default value
    }

    [Fact]
    public async Task CreateSmartPlaylistAsync_WithDuplicateName_ReturnsNull()
    {
        // Arrange
        await _service.CreateSmartPlaylistAsync("Duplicate Test");

        // Act
        var result = await _service.CreateSmartPlaylistAsync("Duplicate Test");

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task CreateSmartPlaylistAsync_DuplicateNameIsCaseInsensitive()
    {
        // Arrange
        await _service.CreateSmartPlaylistAsync("My Playlist");

        // Act
        var result = await _service.CreateSmartPlaylistAsync("MY PLAYLIST");

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task CreateSmartPlaylistAsync_TrimsWhitespace()
    {
        // Act
        var result = await _service.CreateSmartPlaylistAsync("  Trimmed Name  ");

        // Assert
        result.Should().NotBeNull();
        result!.Name.Should().Be("Trimmed Name");
    }

    [Fact]
    public async Task CreateSmartPlaylistAsync_WithEmptyName_ThrowsArgumentException()
    {
        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _service.CreateSmartPlaylistAsync(""));
    }

    [Fact]
    public async Task CreateSmartPlaylistAsync_WithWhitespaceOnlyName_ThrowsArgumentException()
    {
        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _service.CreateSmartPlaylistAsync("   "));
    }

    [Fact]
    public async Task DeleteSmartPlaylistAsync_ExistingPlaylist_ReturnsTrue()
    {
        // Arrange
        var playlist = await _service.CreateSmartPlaylistAsync("To Delete");

        // Act
        var result = await _service.DeleteSmartPlaylistAsync(playlist!.Id);

        // Assert
        result.Should().BeTrue();

        // Verify deletion
        var fetched = await _service.GetSmartPlaylistByIdAsync(playlist.Id);
        fetched.Should().BeNull();
    }

    [Fact]
    public async Task DeleteSmartPlaylistAsync_NonExistingPlaylist_ReturnsFalse()
    {
        // Act
        var result = await _service.DeleteSmartPlaylistAsync(Guid.NewGuid());

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public async Task DeleteSmartPlaylistAsync_AlsoDeletesRules()
    {
        // Arrange
        var playlist = await _service.CreateSmartPlaylistAsync("With Rules");
        await _service.AddRuleAsync(playlist!.Id, SmartPlaylistField.Title, SmartPlaylistOperator.Contains, "test");
        await _service.AddRuleAsync(playlist.Id, SmartPlaylistField.PlayCount, SmartPlaylistOperator.GreaterThan, "10");

        // Act
        await _service.DeleteSmartPlaylistAsync(playlist.Id);

        // Assert
        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var rules = await context.SmartPlaylistRules
            .Where(r => r.SmartPlaylistId == playlist.Id)
            .ToListAsync();
        rules.Should().BeEmpty();
    }

    [Fact]
    public async Task GetSmartPlaylistByIdAsync_ExistingPlaylist_ReturnsPlaylist()
    {
        // Arrange
        var created = await _service.CreateSmartPlaylistAsync("Fetch Test");

        // Act
        var result = await _service.GetSmartPlaylistByIdAsync(created!.Id);

        // Assert
        result.Should().NotBeNull();
        result!.Name.Should().Be("Fetch Test");
    }

    [Fact]
    public async Task GetSmartPlaylistByIdAsync_NonExistingPlaylist_ReturnsNull()
    {
        // Act
        var result = await _service.GetSmartPlaylistByIdAsync(Guid.NewGuid());

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetSmartPlaylistByIdAsync_IncludesRulesOrderedByOrder()
    {
        // Arrange
        var playlist = await _service.CreateSmartPlaylistAsync("With Ordered Rules");
        await _service.AddRuleAsync(playlist!.Id, SmartPlaylistField.Title, SmartPlaylistOperator.Contains, "a");
        await _service.AddRuleAsync(playlist.Id, SmartPlaylistField.Artist, SmartPlaylistOperator.Is, "b");

        // Act
        var result = await _service.GetSmartPlaylistByIdAsync(playlist.Id);

        // Assert
        result!.Rules.Should().HaveCount(2);
        result.Rules.First().Field.Should().Be(SmartPlaylistField.Title);
        result.Rules.Last().Field.Should().Be(SmartPlaylistField.Artist);
    }

    [Fact]
    public async Task GetAllSmartPlaylistsAsync_ReturnsAllPlaylistsOrderedByName()
    {
        // Arrange
        await _service.CreateSmartPlaylistAsync("Zebra");
        await _service.CreateSmartPlaylistAsync("Alpha");
        await _service.CreateSmartPlaylistAsync("Middle");

        // Act
        var results = (await _service.GetAllSmartPlaylistsAsync()).ToList();

        // Assert
        results.Should().HaveCount(3);
        results[0].Name.Should().Be("Alpha");
        results[1].Name.Should().Be("Middle");
        results[2].Name.Should().Be("Zebra");
    }

    [Fact]
    public async Task RenameSmartPlaylistAsync_ValidNewName_ReturnsTrue()
    {
        // Arrange
        var playlist = await _service.CreateSmartPlaylistAsync("Original Name");

        // Act
        var result = await _service.RenameSmartPlaylistAsync(playlist!.Id, "New Name");

        // Assert
        result.Should().BeTrue();

        var fetched = await _service.GetSmartPlaylistByIdAsync(playlist.Id);
        fetched!.Name.Should().Be("New Name");
    }

    [Fact]
    public async Task RenameSmartPlaylistAsync_DuplicateName_ReturnsFalse()
    {
        // Arrange
        await _service.CreateSmartPlaylistAsync("Existing Name");
        var toRename = await _service.CreateSmartPlaylistAsync("To Rename");

        // Act
        var result = await _service.RenameSmartPlaylistAsync(toRename!.Id, "Existing Name");

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public async Task RenameSmartPlaylistAsync_SameNameDifferentCase_ReturnsFalse()
    {
        // Arrange
        await _service.CreateSmartPlaylistAsync("My Playlist");
        var toRename = await _service.CreateSmartPlaylistAsync("Another Playlist");

        // Act
        var result = await _service.RenameSmartPlaylistAsync(toRename!.Id, "MY PLAYLIST");

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public async Task RenameSmartPlaylistAsync_NonExistingPlaylist_ReturnsFalse()
    {
        // Act
        var result = await _service.RenameSmartPlaylistAsync(Guid.NewGuid(), "New Name");

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public async Task RenameSmartPlaylistAsync_EmptyName_ThrowsArgumentException()
    {
        // Arrange
        var playlist = await _service.CreateSmartPlaylistAsync("Test");

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _service.RenameSmartPlaylistAsync(playlist!.Id, ""));
    }

    [Fact]
    public async Task UpdateSmartPlaylistCoverAsync_ValidPlaylist_UpdatesCover()
    {
        // Arrange
        var playlist = await _service.CreateSmartPlaylistAsync("Cover Test");

        // Act
        var result = await _service.UpdateSmartPlaylistCoverAsync(playlist!.Id, "C:\\cover.jpg");

        // Assert
        result.Should().BeTrue();

        var fetched = await _service.GetSmartPlaylistByIdAsync(playlist.Id);
        fetched!.CoverImageUri.Should().Be("C:\\cover.jpg");
    }

    [Fact]
    public async Task UpdateSmartPlaylistCoverAsync_NonExistingPlaylist_ReturnsFalse()
    {
        // Act
        var result = await _service.UpdateSmartPlaylistCoverAsync(Guid.NewGuid(), "C:\\cover.jpg");

        // Assert
        result.Should().BeFalse();
    }

    #endregion

    #region Configuration Tests

    [Fact]
    public async Task SetMatchAllRulesAsync_UpdatesMatchAllRules()
    {
        // Arrange
        var playlist = await _service.CreateSmartPlaylistAsync("Match Test");

        // Act
        var result = await _service.SetMatchAllRulesAsync(playlist!.Id, false);

        // Assert
        result.Should().BeTrue();

        var fetched = await _service.GetSmartPlaylistByIdAsync(playlist.Id);
        fetched!.MatchAllRules.Should().BeFalse();
    }

    [Fact]
    public async Task SetSortOrderAsync_UpdatesSortOrder()
    {
        // Arrange
        var playlist = await _service.CreateSmartPlaylistAsync("Sort Test");

        // Act
        var result = await _service.SetSortOrderAsync(playlist!.Id, SmartPlaylistSortOrder.PlayCountDesc);

        // Assert
        result.Should().BeTrue();

        var fetched = await _service.GetSmartPlaylistByIdAsync(playlist.Id);
        fetched!.SortOrder.Should().Be(SmartPlaylistSortOrder.PlayCountDesc);
    }

    #endregion

    #region Rule Management Tests

    [Fact]
    public async Task AddRuleAsync_ValidPlaylist_AddsRule()
    {
        // Arrange
        var playlist = await _service.CreateSmartPlaylistAsync("Rule Test");

        // Act
        var rule = await _service.AddRuleAsync(
            playlist!.Id,
            SmartPlaylistField.Title,
            SmartPlaylistOperator.Contains,
            "test");

        // Assert
        rule.Should().NotBeNull();
        rule!.Field.Should().Be(SmartPlaylistField.Title);
        rule.Operator.Should().Be(SmartPlaylistOperator.Contains);
        rule.Value.Should().Be("test");
        rule.Order.Should().Be(0);
    }

    [Fact]
    public async Task AddRuleAsync_MultipleRules_CorrectlyOrdersRules()
    {
        // Arrange
        var playlist = await _service.CreateSmartPlaylistAsync("Multi Rule Test");

        // Act
        var rule1 = await _service.AddRuleAsync(playlist!.Id, SmartPlaylistField.Title, SmartPlaylistOperator.Contains, "1");
        var rule2 = await _service.AddRuleAsync(playlist.Id, SmartPlaylistField.Artist, SmartPlaylistOperator.Is, "2");
        var rule3 = await _service.AddRuleAsync(playlist.Id, SmartPlaylistField.Genre, SmartPlaylistOperator.Is, "3");

        // Assert
        rule1!.Order.Should().Be(0);
        rule2!.Order.Should().Be(1);
        rule3!.Order.Should().Be(2);
    }

    [Fact]
    public async Task AddRuleAsync_NonExistingPlaylist_ReturnsNull()
    {
        // Act
        var rule = await _service.AddRuleAsync(
            Guid.NewGuid(),
            SmartPlaylistField.Title,
            SmartPlaylistOperator.Contains,
            "test");

        // Assert
        rule.Should().BeNull();
    }

    [Fact]
    public async Task AddRuleAsync_WithSecondValue_StoresSecondValue()
    {
        // Arrange
        var playlist = await _service.CreateSmartPlaylistAsync("Range Test");

        // Act
        var rule = await _service.AddRuleAsync(
            playlist!.Id,
            SmartPlaylistField.PlayCount,
            SmartPlaylistOperator.IsInRange,
            "10",
            "100");

        // Assert
        rule!.SecondValue.Should().Be("100");
    }

    [Fact]
    public async Task UpdateRuleAsync_ValidRule_UpdatesRule()
    {
        // Arrange
        var playlist = await _service.CreateSmartPlaylistAsync("Update Rule Test");
        var rule = await _service.AddRuleAsync(
            playlist!.Id,
            SmartPlaylistField.Title,
            SmartPlaylistOperator.Contains,
            "old");

        // Act
        var result = await _service.UpdateRuleAsync(
            rule!.Id,
            SmartPlaylistField.Artist,
            SmartPlaylistOperator.Is,
            "new");

        // Assert
        result.Should().BeTrue();

        var fetched = await _service.GetSmartPlaylistByIdAsync(playlist.Id);
        var updatedRule = fetched!.Rules.First();
        updatedRule.Field.Should().Be(SmartPlaylistField.Artist);
        updatedRule.Operator.Should().Be(SmartPlaylistOperator.Is);
        updatedRule.Value.Should().Be("new");
    }

    [Fact]
    public async Task UpdateRuleAsync_NonExistingRule_ReturnsFalse()
    {
        // Act
        var result = await _service.UpdateRuleAsync(
            Guid.NewGuid(),
            SmartPlaylistField.Title,
            SmartPlaylistOperator.Contains,
            "test");

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public async Task RemoveRuleAsync_ValidRule_RemovesRule()
    {
        // Arrange
        var playlist = await _service.CreateSmartPlaylistAsync("Remove Rule Test");
        var rule = await _service.AddRuleAsync(playlist!.Id, SmartPlaylistField.Title, SmartPlaylistOperator.Contains, "test");

        // Act
        var result = await _service.RemoveRuleAsync(rule!.Id);

        // Assert
        result.Should().BeTrue();

        var fetched = await _service.GetSmartPlaylistByIdAsync(playlist.Id);
        fetched!.Rules.Should().BeEmpty();
    }

    [Fact]
    public async Task RemoveRuleAsync_ReindexesRemainingRules()
    {
        // Arrange
        var playlist = await _service.CreateSmartPlaylistAsync("Reindex Test");
        await _service.AddRuleAsync(playlist!.Id, SmartPlaylistField.Title, SmartPlaylistOperator.Contains, "1");
        var rule2 = await _service.AddRuleAsync(playlist.Id, SmartPlaylistField.Artist, SmartPlaylistOperator.Is, "2");
        await _service.AddRuleAsync(playlist.Id, SmartPlaylistField.Genre, SmartPlaylistOperator.Is, "3");

        // Act - Remove middle rule
        await _service.RemoveRuleAsync(rule2!.Id);

        // Assert
        var fetched = await _service.GetSmartPlaylistByIdAsync(playlist.Id);
        var rules = fetched!.Rules.OrderBy(r => r.Order).ToList();
        rules.Should().HaveCount(2);
        rules[0].Order.Should().Be(0);
        rules[0].Value.Should().Be("1");
        rules[1].Order.Should().Be(1);
        rules[1].Value.Should().Be("3");
    }

    [Fact]
    public async Task RemoveRuleAsync_NonExistingRule_ReturnsFalse()
    {
        // Act
        var result = await _service.RemoveRuleAsync(Guid.NewGuid());

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public async Task ReorderRulesAsync_ValidOrder_ReordersRules()
    {
        // Arrange
        var playlist = await _service.CreateSmartPlaylistAsync("Reorder Test");
        var rule1 = await _service.AddRuleAsync(playlist!.Id, SmartPlaylistField.Title, SmartPlaylistOperator.Contains, "1");
        var rule2 = await _service.AddRuleAsync(playlist.Id, SmartPlaylistField.Artist, SmartPlaylistOperator.Is, "2");
        var rule3 = await _service.AddRuleAsync(playlist.Id, SmartPlaylistField.Genre, SmartPlaylistOperator.Is, "3");

        // Act - Reverse order
        var result = await _service.ReorderRulesAsync(playlist.Id, new[] { rule3!.Id, rule2!.Id, rule1!.Id });

        // Assert
        result.Should().BeTrue();

        var fetched = await _service.GetSmartPlaylistByIdAsync(playlist.Id);
        var rules = fetched!.Rules.OrderBy(r => r.Order).ToList();
        rules[0].Value.Should().Be("3");
        rules[1].Value.Should().Be("2");
        rules[2].Value.Should().Be("1");
    }

    #endregion

    #region Query Execution Tests

    [Fact]
    public async Task GetMatchingSongsAsync_ReturnsMatchingSongs()
    {
        // Arrange
        var playlist = await _service.CreateSmartPlaylistAsync("Query Test");
        await _service.AddRuleAsync(playlist!.Id, SmartPlaylistField.IsLoved, SmartPlaylistOperator.IsTrue, null);

        // Act
        var songs = (await _service.GetMatchingSongsAsync(playlist.Id)).ToList();

        // Assert - song1 and song3 are loved
        songs.Should().HaveCount(2);
        songs.Should().OnlyContain(s => s.IsLoved);
    }

    [Fact]
    public async Task GetMatchingSongsAsync_NonExistingPlaylist_ReturnsEmpty()
    {
        // Act
        var songs = (await _service.GetMatchingSongsAsync(Guid.NewGuid())).ToList();

        // Assert
        songs.Should().BeEmpty();
    }

    [Fact]
    public async Task GetMatchingSongsAsync_WithSearchTerm_FiltersResults()
    {
        // Arrange
        var playlist = await _service.CreateSmartPlaylistAsync("Search Test");
        // No rules - should match all songs

        // Act
        var songs = (await _service.GetMatchingSongsAsync(playlist!.Id, "One")).ToList();

        // Assert
        songs.Should().HaveCount(1);
        songs[0].Title.Should().Be("Song One");
    }

    [Fact]
    public async Task GetMatchingSongsPagedAsync_ReturnsPaginatedResults()
    {
        // Arrange
        var playlist = await _service.CreateSmartPlaylistAsync("Paged Test");
        // No rules - matches all 3 songs

        // Act
        var result = await _service.GetMatchingSongsPagedAsync(playlist!.Id, 1, 2);

        // Assert
        result.Items.Should().HaveCount(2);
        result.TotalCount.Should().Be(3);
        result.PageNumber.Should().Be(1);
        result.PageSize.Should().Be(2);
        result.TotalPages.Should().Be(2);
    }

    [Fact]
    public async Task GetMatchingSongsPagedAsync_SecondPage_ReturnsRemainingItems()
    {
        // Arrange
        var playlist = await _service.CreateSmartPlaylistAsync("Page 2 Test");

        // Act
        var result = await _service.GetMatchingSongsPagedAsync(playlist!.Id, 2, 2);

        // Assert
        result.Items.Should().HaveCount(1);
        result.PageNumber.Should().Be(2);
    }

    [Fact]
    public async Task GetMatchingSongsPagedAsync_InvalidPaging_SanitizesToValidValues()
    {
        // Arrange
        var playlist = await _service.CreateSmartPlaylistAsync("Invalid Page Test");

        // Act
        var result = await _service.GetMatchingSongsPagedAsync(playlist!.Id, 0, -5);

        // Assert
        result.PageNumber.Should().Be(1);
        result.PageSize.Should().Be(50); // Default page size when invalid
    }

    [Fact]
    public async Task GetMatchingSongsPagedAsync_NonExistingPlaylist_ReturnsEmptyResult()
    {
        // Act
        var result = await _service.GetMatchingSongsPagedAsync(Guid.NewGuid(), 1, 10);

        // Assert
        result.Items.Should().BeEmpty();
        result.TotalCount.Should().Be(0);
    }

    [Fact]
    public async Task GetMatchingSongCountAsync_ById_ReturnsCorrectCount()
    {
        // Arrange
        var playlist = await _service.CreateSmartPlaylistAsync("Count Test");
        await _service.AddRuleAsync(playlist!.Id, SmartPlaylistField.PlayCount, SmartPlaylistOperator.GreaterThan, "75");

        // Act
        var count = await _service.GetMatchingSongCountAsync(playlist.Id);

        // Assert - song1 (100) and song3 (200)
        count.Should().Be(2);
    }

    [Fact]
    public async Task GetMatchingSongCountAsync_NonExistingPlaylist_ReturnsZero()
    {
        // Act
        var count = await _service.GetMatchingSongCountAsync(Guid.NewGuid());

        // Assert
        count.Should().Be(0);
    }

    [Fact]
    public async Task GetMatchingSongCountAsync_ByObject_ReturnsCorrectCount()
    {
        // Arrange
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

        // Act
        var count = await _service.GetMatchingSongCountAsync(playlist);

        // Assert - song1 and song3 have Rock genre
        count.Should().Be(2);
    }

    [Fact]
    public async Task GetMatchingSongIdsAsync_ReturnsMatchingIds()
    {
        // Arrange
        var playlist = await _service.CreateSmartPlaylistAsync("IDs Test");
        await _service.AddRuleAsync(playlist!.Id, SmartPlaylistField.Genre, SmartPlaylistOperator.Is, "Pop");

        // Act
        var ids = await _service.GetMatchingSongIdsAsync(playlist.Id);

        // Assert - only song2 has Pop genre
        ids.Should().HaveCount(1);
    }

    [Fact]
    public async Task GetMatchingSongIdsAsync_NonExistingPlaylist_ReturnsEmptyList()
    {
        // Act
        var ids = await _service.GetMatchingSongIdsAsync(Guid.NewGuid());

        // Assert
        ids.Should().BeEmpty();
    }

    [Fact]
    public async Task GetAllMatchingSongCountsAsync_ReturnsCountsForAllPlaylists()
    {
        // Arrange
        var playlist1 = await _service.CreateSmartPlaylistAsync("All Count 1");
        await _service.AddRuleAsync(playlist1!.Id, SmartPlaylistField.IsLoved, SmartPlaylistOperator.IsTrue, null);

        var playlist2 = await _service.CreateSmartPlaylistAsync("All Count 2");
        await _service.AddRuleAsync(playlist2!.Id, SmartPlaylistField.Genre, SmartPlaylistOperator.Is, "Pop");

        // Act
        var counts = await _service.GetAllMatchingSongCountsAsync();

        // Assert
        counts.Should().HaveCount(2);
        counts[playlist1.Id].Should().Be(2); // 2 loved songs
        counts[playlist2.Id].Should().Be(1); // 1 Pop song
    }

    [Fact]
    public async Task GetAllMatchingSongCountsAsync_NoPlaylists_ReturnsEmptyDictionary()
    {
        // Act
        var counts = await _service.GetAllMatchingSongCountsAsync();

        // Assert
        counts.Should().BeEmpty();
    }

    #endregion

    #region Edge Cases

    [Fact]
    public async Task UpdateSmartPlaylistAsync_ValidPlaylist_UpdatesProperties()
    {
        // Arrange
        var playlist = await _service.CreateSmartPlaylistAsync("Update Test");
        playlist!.Name = "Updated Name";
        playlist.Description = "Updated Description";
        playlist.MatchAllRules = false;
        playlist.SortOrder = SmartPlaylistSortOrder.Random;

        // Act
        var result = await _service.UpdateSmartPlaylistAsync(playlist);

        // Assert
        result.Should().BeTrue();

        var fetched = await _service.GetSmartPlaylistByIdAsync(playlist.Id);
        fetched!.Name.Should().Be("Updated Name");
        fetched.Description.Should().Be("Updated Description");
        fetched.MatchAllRules.Should().BeFalse();
        fetched.SortOrder.Should().Be(SmartPlaylistSortOrder.Random);
    }

    [Fact]
    public async Task UpdateSmartPlaylistAsync_NonExistingPlaylist_ReturnsFalse()
    {
        // Arrange
        var playlist = new SmartPlaylist { Id = Guid.NewGuid(), Name = "Non-existent" };

        // Act
        var result = await _service.UpdateSmartPlaylistAsync(playlist);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public async Task OperationsUpdateDateModified()
    {
        // Arrange
        var playlist = await _service.CreateSmartPlaylistAsync("Date Test");
        var originalModified = playlist!.DateModified;
        
        // Wait a tiny bit to ensure different timestamp
        await Task.Delay(10);

        // Act
        await _service.RenameSmartPlaylistAsync(playlist.Id, "New Date Test");

        // Assert
        var fetched = await _service.GetSmartPlaylistByIdAsync(playlist.Id);
        fetched!.DateModified.Should().BeAfter(originalModified);
    }

    #endregion

    #region Additional Edge Cases

    [Fact]
    public async Task RenameSmartPlaylistAsync_ToSameName_Succeeds()
    {
        // Arrange - Renaming to exactly the same name should be allowed
        var playlist = await _service.CreateSmartPlaylistAsync("Same Name");

        // Act
        var result = await _service.RenameSmartPlaylistAsync(playlist!.Id, "Same Name");

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public async Task GetMatchingSongsPagedAsync_VeryLargePageSize_IsCapped()
    {
        // Arrange
        var playlist = await _service.CreateSmartPlaylistAsync("Large Page Test");

        // Act - Request a very large page size (should be capped at 500)
        var result = await _service.GetMatchingSongsPagedAsync(playlist!.Id, 1, 10000);

        // Assert
        result.PageSize.Should().Be(500); // Capped at max
    }

    [Fact]
    public async Task GetMatchingSongsPagedAsync_PageBeyondData_ReturnsEmptyItems()
    {
        // Arrange
        var playlist = await _service.CreateSmartPlaylistAsync("Beyond Page Test");

        // Act - Request page 100 when only 3 songs exist
        var result = await _service.GetMatchingSongsPagedAsync(playlist!.Id, 100, 10);

        // Assert
        result.Items.Should().BeEmpty();
        result.TotalCount.Should().Be(3); // Total should still reflect actual count
    }

    [Fact]
    public async Task UpdateSmartPlaylistCoverAsync_ClearCover_SetsToNull()
    {
        // Arrange
        var playlist = await _service.CreateSmartPlaylistAsync("Clear Cover Test");
        await _service.UpdateSmartPlaylistCoverAsync(playlist!.Id, "C:\\cover.jpg");

        // Act - Clear the cover by setting to null
        var result = await _service.UpdateSmartPlaylistCoverAsync(playlist.Id, null);

        // Assert
        result.Should().BeTrue();

        var fetched = await _service.GetSmartPlaylistByIdAsync(playlist.Id);
        fetched!.CoverImageUri.Should().BeNull();
    }

    [Fact]
    public async Task SetMatchAllRulesAsync_NonExistingPlaylist_ReturnsFalse()
    {
        // Act
        var result = await _service.SetMatchAllRulesAsync(Guid.NewGuid(), true);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public async Task SetSortOrderAsync_NonExistingPlaylist_ReturnsFalse()
    {
        // Act
        var result = await _service.SetSortOrderAsync(Guid.NewGuid(), SmartPlaylistSortOrder.Random);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public async Task ReorderRulesAsync_NonExistingPlaylist_ReturnsFalse()
    {
        // Act
        var result = await _service.ReorderRulesAsync(Guid.NewGuid(), new[] { Guid.NewGuid() });

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public async Task ReorderRulesAsync_PartialList_OnlyReordersProvidedRules()
    {
        // Arrange
        var playlist = await _service.CreateSmartPlaylistAsync("Partial Reorder Test");
        var rule1 = await _service.AddRuleAsync(playlist!.Id, SmartPlaylistField.Title, SmartPlaylistOperator.Contains, "1");
        var rule2 = await _service.AddRuleAsync(playlist.Id, SmartPlaylistField.Artist, SmartPlaylistOperator.Is, "2");
        var rule3 = await _service.AddRuleAsync(playlist.Id, SmartPlaylistField.Genre, SmartPlaylistOperator.Is, "3");

        // Act - Only reorder 2 of 3 rules (swap rule2 and rule3)
        await _service.ReorderRulesAsync(playlist.Id, new[] { rule1!.Id, rule3!.Id, rule2!.Id });

        // Assert
        var fetched = await _service.GetSmartPlaylistByIdAsync(playlist.Id);
        var rules = fetched!.Rules.OrderBy(r => r.Order).ToList();
        rules[0].Value.Should().Be("1");
        rules[1].Value.Should().Be("3");
        rules[2].Value.Should().Be("2");
    }

    [Fact]
    public async Task GetMatchingSongsAsync_NoRules_ReturnsAllSongs()
    {
        // Arrange - Create playlist with no rules
        var playlist = await _service.CreateSmartPlaylistAsync("No Rules Test");

        // Act
        var songs = (await _service.GetMatchingSongsAsync(playlist!.Id)).ToList();

        // Assert
        songs.Should().HaveCount(3); // All seeded songs
    }

    [Fact]
    public async Task AddRuleAsync_AfterRemovingAllRules_StartsFromZeroOrder()
    {
        // Arrange
        var playlist = await _service.CreateSmartPlaylistAsync("Reset Order Test");
        var rule1 = await _service.AddRuleAsync(playlist!.Id, SmartPlaylistField.Title, SmartPlaylistOperator.Contains, "old");
        await _service.RemoveRuleAsync(rule1!.Id);

        // Act - Add new rule after all were removed
        var newRule = await _service.AddRuleAsync(playlist.Id, SmartPlaylistField.Title, SmartPlaylistOperator.Contains, "new");

        // Assert - Order should start from 0 again
        newRule!.Order.Should().Be(0);
    }

    [Fact]
    public async Task GetMatchingSongCountAsync_WithSearchTerm_FiltersCount()
    {
        // Arrange
        var playlist = await _service.CreateSmartPlaylistAsync("Search Count Test");
        await _service.AddRuleAsync(playlist!.Id, SmartPlaylistField.Genre, SmartPlaylistOperator.Is, "Rock");

        // Act
        var countWithSearch = await _service.GetMatchingSongCountAsync(playlist.Id, "One");
        var countWithoutSearch = await _service.GetMatchingSongCountAsync(playlist.Id);

        // Assert
        countWithSearch.Should().Be(1); // Only "Song One" matches both Rock genre and "One" search
        countWithoutSearch.Should().Be(2); // Both Rock songs
    }

    #endregion

    #region Concurrency Tests

    /// <summary>
    ///     Verifies that multiple concurrent operations on different playlists don't interfere with each other.
    /// </summary>
    [Fact]
    public async Task ConcurrentOperations_OnDifferentPlaylists_AllSucceed()
    {
        // Arrange
        var playlist1 = await _service.CreateSmartPlaylistAsync("Playlist 1");
        var playlist2 = await _service.CreateSmartPlaylistAsync("Playlist 2");
        var playlist3 = await _service.CreateSmartPlaylistAsync("Playlist 3");

        // Act - Add rules to all playlists concurrently
        var tasks = new Task[]
        {
            _service.AddRuleAsync(playlist1!.Id, SmartPlaylistField.PlayCount, SmartPlaylistOperator.GreaterThan, "10"),
            _service.AddRuleAsync(playlist2!.Id, SmartPlaylistField.Rating, SmartPlaylistOperator.Equals, "5"),
            _service.AddRuleAsync(playlist3!.Id, SmartPlaylistField.Year, SmartPlaylistOperator.LessThan, "2000"),
            _service.RenameSmartPlaylistAsync(playlist1.Id, "Renamed Playlist 1"),
            _service.SetMatchAllRulesAsync(playlist2.Id, false)
        };

        var results = await Task.WhenAll(tasks.Select(t => t.ContinueWith(r => r.IsCompletedSuccessfully)));

        // Assert
        results.Should().AllSatisfy(success => success.Should().BeTrue());

        var refreshedPlaylist1 = await _service.GetSmartPlaylistByIdAsync(playlist1.Id);
        refreshedPlaylist1!.Name.Should().Be("Renamed Playlist 1");
        refreshedPlaylist1.Rules.Should().HaveCount(1);

        var refreshedPlaylist2 = await _service.GetSmartPlaylistByIdAsync(playlist2.Id);
        refreshedPlaylist2!.MatchAllRules.Should().BeFalse();
        refreshedPlaylist2.Rules.Should().HaveCount(1);

        var refreshedPlaylist3 = await _service.GetSmartPlaylistByIdAsync(playlist3.Id);
        refreshedPlaylist3!.Rules.Should().HaveCount(1);
    }

    /// <summary>
    ///     Verifies that adding multiple rules to the same playlist concurrently maintains correct ordering.
    /// </summary>
    [Fact]
    public async Task AddRuleAsync_ConcurrentAddsToSamePlaylist_MaintainsCorrectOrdering()
    {
        // Arrange
        var playlist = await _service.CreateSmartPlaylistAsync("Concurrent Rules Test");

        // Act - Add 5 rules concurrently
        var addTasks = Enumerable.Range(1, 5)
            .Select(i => _service.AddRuleAsync(
                playlist!.Id,
                SmartPlaylistField.PlayCount,
                SmartPlaylistOperator.GreaterThan,
                i.ToString()))
            .ToList();

        var rules = await Task.WhenAll(addTasks);

        // Assert - All rules should be added and have unique order values
        var refreshedPlaylist = await _service.GetSmartPlaylistByIdAsync(playlist!.Id);
        refreshedPlaylist!.Rules.Should().HaveCount(5);

        var orders = refreshedPlaylist.Rules.Select(r => r.Order).ToList();
        orders.Should().OnlyHaveUniqueItems();
        orders.Should().BeEquivalentTo(new[] { 0, 1, 2, 3, 4 });
    }

    #endregion

    #region ReorderRulesAsync Edge Cases

    /// <summary>
    ///     Verifies that ReorderRulesAsync with duplicate rule IDs only processes each rule once.
    /// </summary>
    [Fact]
    public async Task ReorderRulesAsync_WithDuplicateIds_IgnoresDuplicates()
    {
        // Arrange
        var playlist = await _service.CreateSmartPlaylistAsync("Duplicate Test");
        var rule1 = await _service.AddRuleAsync(playlist!.Id, SmartPlaylistField.Title, SmartPlaylistOperator.Contains, "a");
        var rule2 = await _service.AddRuleAsync(playlist.Id, SmartPlaylistField.Title, SmartPlaylistOperator.Contains, "b");
        
        // Act - Include rule1's ID twice
        var duplicateOrderedIds = new[] { rule2!.Id, rule1!.Id, rule1.Id };
        await _service.ReorderRulesAsync(playlist.Id, duplicateOrderedIds);

        // Assert
        var refreshedPlaylist = await _service.GetSmartPlaylistByIdAsync(playlist.Id);
        refreshedPlaylist!.Rules.Should().HaveCount(2);
        var orderedRules = refreshedPlaylist.Rules.OrderBy(r => r.Order).ToList();
        orderedRules[0].Id.Should().Be(rule2.Id); // rule2 first
        orderedRules[1].Id.Should().Be(rule1.Id); // rule1 second
    }

    /// <summary>
    ///     Verifies that ReorderRulesAsync with rule IDs from a different playlist has no effect.
    /// </summary>
    [Fact]
    public async Task ReorderRulesAsync_WithRulesFromDifferentPlaylist_OnlyReordersMatchingRules()
    {
        // Arrange
        var playlist1 = await _service.CreateSmartPlaylistAsync("Playlist One");
        var playlist2 = await _service.CreateSmartPlaylistAsync("Playlist Two");
        var rule1 = await _service.AddRuleAsync(playlist1!.Id, SmartPlaylistField.Title, SmartPlaylistOperator.Contains, "a");
        var rule2 = await _service.AddRuleAsync(playlist1.Id, SmartPlaylistField.Title, SmartPlaylistOperator.Contains, "b");
        var foreignRule = await _service.AddRuleAsync(playlist2!.Id, SmartPlaylistField.Title, SmartPlaylistOperator.Contains, "c");

        // Act - Try to include a rule from playlist2 in playlist1's reorder
        await _service.ReorderRulesAsync(playlist1.Id, new[] { rule2!.Id, foreignRule!.Id, rule1!.Id });

        // Assert - Only playlist1's rules should be affected
        var refreshedPlaylist1 = await _service.GetSmartPlaylistByIdAsync(playlist1.Id);
        refreshedPlaylist1!.Rules.Should().HaveCount(2);
        var orderedRules = refreshedPlaylist1.Rules.OrderBy(r => r.Order).ToList();
        orderedRules[0].Id.Should().Be(rule2.Id);
        orderedRules[1].Id.Should().Be(rule1.Id);
    }

    /// <summary>
    ///     Verifies that ReorderRulesAsync with an empty array doesn't modify existing rules.
    /// </summary>
    [Fact]
    public async Task ReorderRulesAsync_WithEmptyArray_DoesNotModifyRules()
    {
        // Arrange
        var playlist = await _service.CreateSmartPlaylistAsync("Empty Reorder Test");
        var rule1 = await _service.AddRuleAsync(playlist!.Id, SmartPlaylistField.Title, SmartPlaylistOperator.Contains, "a");
        var rule2 = await _service.AddRuleAsync(playlist.Id, SmartPlaylistField.Title, SmartPlaylistOperator.Contains, "b");
        
        // Act
        await _service.ReorderRulesAsync(playlist.Id, Array.Empty<Guid>());

        // Assert - Original order should be preserved
        var refreshedPlaylist = await _service.GetSmartPlaylistByIdAsync(playlist.Id);
        var orderedRules = refreshedPlaylist!.Rules.OrderBy(r => r.Order).ToList();
        orderedRules[0].Id.Should().Be(rule1!.Id);
        orderedRules[1].Id.Should().Be(rule2!.Id);
    }

    #endregion

    #region Description Handling Tests

    /// <summary>
    ///     Verifies that creating a playlist with an empty description works correctly.
    /// </summary>
    [Fact]
    public async Task UpdateSmartPlaylistAsync_WithEmptyDescription_SetsDescriptionToEmpty()
    {
        // Arrange
        var playlist = await _service.CreateSmartPlaylistAsync("Description Test");
        playlist!.Description = "Initial description";

        using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            var dbPlaylist = await context.SmartPlaylists.FindAsync(playlist.Id);
            dbPlaylist!.Description = "Initial description";
            await context.SaveChangesAsync();
        }

        // Act - Update with empty description
        playlist.Description = "";
        var success = await _service.UpdateSmartPlaylistAsync(playlist);

        // Assert
        success.Should().BeTrue();
        var refreshedPlaylist = await _service.GetSmartPlaylistByIdAsync(playlist.Id);
        refreshedPlaylist!.Description.Should().Be("");
    }

    /// <summary>
    ///     Verifies that creating a playlist with a null description works correctly.
    /// </summary>
    [Fact]
    public async Task UpdateSmartPlaylistAsync_WithNullDescription_SetsDescriptionToNull()
    {
        // Arrange
        var playlist = await _service.CreateSmartPlaylistAsync("Null Description Test");

        using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            var dbPlaylist = await context.SmartPlaylists.FindAsync(playlist!.Id);
            dbPlaylist!.Description = "Initial description";
            await context.SaveChangesAsync();
        }

        // Act - Update with null description
        playlist!.Description = null;
        await _service.UpdateSmartPlaylistAsync(playlist);

        // Assert
        var refreshedPlaylist = await _service.GetSmartPlaylistByIdAsync(playlist.Id);
        refreshedPlaylist!.Description.Should().BeNull();
    }

    #endregion
}
