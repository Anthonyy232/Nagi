using System.Reflection;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Implementations;
using Nagi.Core.Tests.Utils;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Nagi.Core.Tests;

/// <summary>
///     Provides comprehensive unit tests for the <see cref="OfflineScrobbleService" />.
///     These tests leverage an in-memory SQLite database via <see cref="DbContextFactoryTestHelper" />
///     to ensure realistic database interactions while maintaining test isolation. All external
///     dependencies are mocked to focus testing on the service's business logic.
/// </summary>
public class OfflineScrobbleServiceTests : IDisposable
{
    /// <summary>
    ///     A helper that provides a clean, in-memory SQLite database context for each test.
    /// </summary>
    private readonly DbContextFactoryTestHelper _dbHelper;

    private readonly ILogger<OfflineScrobbleService> _logger;

    // Mocks for external dependencies, enabling isolated testing of the service's logic.
    private readonly ILastFmScrobblerService _scrobblerService;

    /// <summary>
    ///     The instance of the service under test.
    /// </summary>
    private readonly OfflineScrobbleService _service;

    private readonly ISettingsService _settingsService;

    /// <summary>
    ///     Initializes a new instance of the <see cref="OfflineScrobbleServiceTests" /> class.
    ///     This constructor sets up the required mocks, the in-memory database, and instantiates
    ///     the <see cref="OfflineScrobbleService" /> with these test dependencies.
    /// </summary>
    public OfflineScrobbleServiceTests()
    {
        _scrobblerService = Substitute.For<ILastFmScrobblerService>();
        _settingsService = Substitute.For<ISettingsService>();
        _dbHelper = new DbContextFactoryTestHelper();
        _logger = Substitute.For<ILogger<OfflineScrobbleService>>();

        // Default setup for mocks to represent a "happy path"
        _settingsService.GetLastFmScrobblingEnabledAsync().Returns(true);
        _scrobblerService.ScrobbleAsync(Arg.Any<Song>(), Arg.Any<DateTime>()).Returns(true);

        _service = new OfflineScrobbleService(
            _dbHelper.ContextFactory,
            _scrobblerService,
            _settingsService,
            _logger);
    }

    /// <summary>
    ///     Performs application-defined tasks associated with freeing, releasing, or resetting
    ///     unmanaged resources. This method ensures test isolation by disposing the in-memory
    ///     database and the service under test after each test execution.
    /// </summary>
    public void Dispose()
    {
        if (_service != null) _service.Dispose();
        _dbHelper.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    ///     Helper method to seed the in-memory database with a specified number of listen history
    ///     entries that are eligible for scrobbling.
    /// </summary>
    /// <param name="count">The number of pending scrobbles to create.</param>
    /// <returns>A list of the created <see cref="ListenHistory" /> entities.</returns>
    private async Task<List<ListenHistory>> SeedPendingScrobblesAsync(int count)
    {
        var artist = new Artist { Name = "Test Artist" };
        var album = new Album { Title = "Test Album" };
        album.AlbumArtists.Add(new AlbumArtist { Artist = artist, Order = 0 });
        album.SyncDenormalizedFields();
        var folder = new Folder { Name = "Test Folder", Path = "C:/Music/TestFolder" };
        var songs = Enumerable.Range(1, count)
            .Select(i =>
            {
                var s = new Song
                {
                    Title = $"Song {i}",
                    Album = album,
                    Folder = folder,
                    FilePath = $"C:/Music/TestFolder/Song{i}.mp3" // Unique file path per song
                };
                s.SongArtists.Add(new SongArtist { Artist = artist, Order = 0 });
                s.SyncDenormalizedFields();
                return s;
            })
            .ToList();

        var historyEntries = songs.Select((song, index) => new ListenHistory
        {
            Song = song,
            ListenTimestampUtc = DateTime.UtcNow.AddMinutes(-(count - index)), // Ensure chronological order
            IsEligibleForScrobbling = true,
            IsScrobbled = false
        }).ToList();

        await using var context = _dbHelper.ContextFactory.CreateDbContext();

        // Add all required parent entities to the context.
        context.Folders.Add(folder);
        context.Artists.Add(artist);
        context.Albums.Add(album);
        context.ListenHistory.AddRange(historyEntries);
        await context.SaveChangesAsync();

        return historyEntries;
    }

    #region ProcessQueueAsync Tests

    /// <summary>
    ///     Verifies that <see cref="OfflineScrobbleService.ProcessQueueAsync" /> exits immediately
    ///     without performing any actions if scrobbling is disabled in the settings.
    /// </summary>
    [Fact]
    public async Task ProcessQueueAsync_WhenScrobblingIsDisabled_ExitsEarly()
    {
        // Arrange
        _settingsService.GetLastFmScrobblingEnabledAsync().Returns(false);
        await SeedPendingScrobblesAsync(1);

        // Act
        await _service.ProcessQueueAsync();

        // Assert: Verify that no attempt was made to scrobble.
        await _scrobblerService.DidNotReceive().ScrobbleAsync(Arg.Any<Song>(), Arg.Any<DateTime>());
    }

    /// <summary>
    ///     Verifies that <see cref="OfflineScrobbleService.ProcessQueueAsync" /> does not call the
    ///     scrobbler service when there are no pending scrobbles in the database.
    /// </summary>
    [Fact]
    public async Task ProcessQueueAsync_WithNoPendingScrobbles_DoesNothing()
    {
        // Arrange: The database is empty by default.

        // Act
        await _service.ProcessQueueAsync();

        // Assert: Verify that the scrobbler service was never called.
        await _scrobblerService.DidNotReceive().ScrobbleAsync(Arg.Any<Song>(), Arg.Any<DateTime>());
    }

    /// <summary>
    ///     Verifies that <see cref="OfflineScrobbleService.ProcessQueueAsync" /> successfully processes
    ///     all pending scrobbles, calls the external scrobbler service for each, and updates their
    ///     status in the database.
    /// </summary>
    [Fact]
    public async Task ProcessQueueAsync_WithPendingScrobbles_SuccessfullyScrobblesAll()
    {
        // Arrange
        var pendingScrobbles = await SeedPendingScrobblesAsync(3);

        // Act
        await _service.ProcessQueueAsync();

        // Assert: Verify that the scrobbler was called for each pending entry.
        await _scrobblerService.Received(3).ScrobbleAsync(Arg.Any<Song>(), Arg.Any<DateTime>());
        await _scrobblerService.Received(1).ScrobbleAsync(
            Arg.Is<Song>(s => s.Id == pendingScrobbles[0].SongId),
            pendingScrobbles[0].ListenTimestampUtc);
        await _scrobblerService.Received(1).ScrobbleAsync(
            Arg.Is<Song>(s => s.Id == pendingScrobbles[2].SongId),
            pendingScrobbles[2].ListenTimestampUtc);

        // Assert: Verify that all entries were marked as scrobbled in the database.
        await using var assertContext = _dbHelper.ContextFactory.CreateDbContext();
        var history = await assertContext.ListenHistory.ToListAsync();
        history.Should().HaveCount(3);
        history.Should().OnlyContain(h => h.IsScrobbled);
    }

    /// <summary>
    ///     Verifies that if the external scrobbler service returns a failure for an item,
    ///     <see cref="OfflineScrobbleService.ProcessQueueAsync" /> stops processing the queue but
    ///     correctly saves the status of any items that were successfully scrobbled before the failure.
    /// </summary>
    [Fact]
    public async Task ProcessQueueAsync_WhenScrobblerFails_StopsProcessingAndSavesSuccesses()
    {
        // Arrange
        var pendingScrobbles = await SeedPendingScrobblesAsync(3);
        var successfulSong = pendingScrobbles[0].Song!;
        var failedSong = pendingScrobbles[1].Song!;

        // Mock the scrobbler to fail on the second song.
        _scrobblerService.ScrobbleAsync(successfulSong, Arg.Any<DateTime>()).Returns(true);
        _scrobblerService.ScrobbleAsync(failedSong, Arg.Any<DateTime>()).Returns(false);

        // Act
        await _service.ProcessQueueAsync();

        // Assert: The scrobbler should have been called for the first two songs, but not the third.
        await _scrobblerService.Received(1).ScrobbleAsync(successfulSong, Arg.Any<DateTime>());
        await _scrobblerService.Received(1).ScrobbleAsync(failedSong, Arg.Any<DateTime>());
        await _scrobblerService.DidNotReceive().ScrobbleAsync(pendingScrobbles[2].Song!, Arg.Any<DateTime>());

        // Assert: Only the first song should be marked as scrobbled in the database.
        await using var assertContext = _dbHelper.ContextFactory.CreateDbContext();
        (await assertContext.ListenHistory.FindAsync(pendingScrobbles[0].Id))!.IsScrobbled.Should().BeTrue();
        (await assertContext.ListenHistory.FindAsync(pendingScrobbles[1].Id))!.IsScrobbled.Should().BeFalse();
        (await assertContext.ListenHistory.FindAsync(pendingScrobbles[2].Id))!.IsScrobbled.Should().BeFalse();
    }

    /// <summary>
    ///     Verifies that if the external scrobbler service throws an exception,
    ///     <see cref="OfflineScrobbleService.ProcessQueueAsync" /> gracefully stops processing,
    ///     saves the state of successfully scrobbled items, and does not crash.
    /// </summary>
    [Fact]
    public async Task ProcessQueueAsync_WhenScrobblerThrows_StopsProcessingAndSavesSuccesses()
    {
        // Arrange
        var pendingScrobbles = await SeedPendingScrobblesAsync(3);
        var successfulSong = pendingScrobbles[0].Song!;
        var exceptionSong = pendingScrobbles[1].Song!;

        // Mock the scrobbler to throw an exception on the second song.
        _scrobblerService.ScrobbleAsync(successfulSong, Arg.Any<DateTime>()).Returns(true);
        _scrobblerService.ScrobbleAsync(exceptionSong, Arg.Any<DateTime>())
            .ThrowsAsync(new HttpRequestException("Network error"));

        // Act
        await _service.ProcessQueueAsync();

        // Assert: The scrobbler should have been called for the first two songs, but not the third.
        await _scrobblerService.Received(1).ScrobbleAsync(successfulSong, Arg.Any<DateTime>());
        await _scrobblerService.Received(1).ScrobbleAsync(exceptionSong, Arg.Any<DateTime>());
        await _scrobblerService.DidNotReceive().ScrobbleAsync(pendingScrobbles[2].Song!, Arg.Any<DateTime>());

        // Assert: Only the first song should be marked as scrobbled in the database.
        await using var assertContext = _dbHelper.ContextFactory.CreateDbContext();
        (await assertContext.ListenHistory.FindAsync(pendingScrobbles[0].Id))!.IsScrobbled.Should().BeTrue();
        (await assertContext.ListenHistory.FindAsync(pendingScrobbles[1].Id))!.IsScrobbled.Should().BeFalse();
    }

    /// <summary>
    ///     Verifies that <see cref="OfflineScrobbleService.ProcessQueueAsync" /> uses a lock to prevent
    ///     concurrent executions, ensuring that a second call exits immediately if a processing
    ///     operation is already in progress.
    /// </summary>
    [Fact]
    public async Task ProcessQueueAsync_WhenAlreadyRunning_ExitsImmediately()
    {
        // Arrange
        await SeedPendingScrobblesAsync(1);
        var tcs = new TaskCompletionSource<bool>();

        // Configure the mock to hang, simulating a long-running operation.
        _scrobblerService.ScrobbleAsync(Arg.Any<Song>(), Arg.Any<DateTime>()).Returns(tcs.Task);

        // Act
        var firstCall = _service.ProcessQueueAsync();
        var secondCall = _service.ProcessQueueAsync(); // This should hit the lock and exit.

        // Allow the first call to complete.
        tcs.SetResult(true);
        await Task.WhenAll(firstCall, secondCall);

        // Assert: The core logic (scrobbling) should only have been executed once.
        await _scrobblerService.Received(1).ScrobbleAsync(Arg.Any<Song>(), Arg.Any<DateTime>());
    }

    /// <summary>
    ///     Verifies that <see cref="OfflineScrobbleService.ProcessQueueAsync" /> respects a
    ///     <see cref="CancellationToken" /> and stops processing if cancellation is requested.
    /// </summary>
    [Fact]
    public async Task ProcessQueueAsync_WhenCancelled_StopsProcessing()
    {
        // Arrange
        await SeedPendingScrobblesAsync(2);
        var cts = new CancellationTokenSource();
        cts.Cancel(); // Cancel before the operation starts.

        // Act
        await _service.ProcessQueueAsync(cts.Token);

        // Assert: No scrobbling should have occurred.
        await _scrobblerService.DidNotReceive().ScrobbleAsync(Arg.Any<Song>(), Arg.Any<DateTime>());
    }

    #endregion

    #region Event Handling and Dispose Tests

    /// <summary>
    ///     Verifies that the service correctly subscribes to the
    ///     <see cref="ISettingsService.LastFmSettingsChanged" /> event and triggers a queue
    ///     processing operation when the event is raised.
    /// </summary>
    [Fact]
    public async Task OnLastFmSettingsChanged_WhenEventFires_TriggersQueueProcessing()
    {
        // Arrange: No pending scrobbles, so ProcessQueueAsync will be quick.
        // We just want to verify it was called.

        // Act: Raise the event on the mock settings service.
        _settingsService.LastFmSettingsChanged += Raise.Event<Action>();

        // Give the fire-and-forget task a moment to run.
        await Task.Delay(50);

        // Assert: The easiest way to confirm ProcessQueueAsync was triggered is to check
        // if it tried to read the scrobbling setting.
        await _settingsService.Received(1).GetLastFmScrobblingEnabledAsync();
    }

    /// <summary>
    ///     Verifies that the <see cref="OfflineScrobbleService.Dispose" /> method correctly
    ///     unsubscribes from the <see cref="ISettingsService.LastFmSettingsChanged" /> event to
    ///     prevent memory leaks and unintended behavior after disposal.
    /// </summary>
    [Fact]
    public async Task Dispose_WhenCalled_UnsubscribesFromSettingsChangedEvent()
    {
        // Arrange
        _service.Dispose();
        // Prevent double disposal in test class
        typeof(OfflineScrobbleServiceTests)
            .GetField("_service", BindingFlags.NonPublic | BindingFlags.Instance)
            ?.SetValue(this, null);

        // Act: Raise the event on the mock settings service AFTER disposing.
        _settingsService.LastFmSettingsChanged += Raise.Event<Action>();
        await Task.Delay(50);

        // Assert: If the handler was correctly unsubscribed, ProcessQueueAsync should not
        // have been triggered, and thus the setting should not have been read.
        await _settingsService.DidNotReceive().GetLastFmScrobblingEnabledAsync();
    }

    #endregion

    private async Task<ListenHistory> SeedMultiArtistPendingScrobbleAsync()
    {
        var artist1 = new Artist { Name = "Artist 1" };
        var artist2 = new Artist { Name = "Artist 2" };
        var folder = new Folder { Name = "Test Folder", Path = "C:/Music/TestFolder" };
        var song = new Song { Title = "Multi Song", Folder = folder, FilePath = "C:/Music/TestFolder/Multi.mp3" };
        song.SongArtists.Add(new SongArtist { Artist = artist1, Order = 0 });
        song.SongArtists.Add(new SongArtist { Artist = artist2, Order = 1 });
        song.SyncDenormalizedFields();

        var entry = new ListenHistory
        {
            Song = song,
            ListenTimestampUtc = DateTime.UtcNow.AddMinutes(-10),
            IsEligibleForScrobbling = true,
            IsScrobbled = false
        };

        await using var context = _dbHelper.ContextFactory.CreateDbContext();
        context.ListenHistory.Add(entry);
        await context.SaveChangesAsync();
        return entry;
    }

    [Fact]
    public async Task ProcessQueueAsync_WithMultiArtistSong_ScrobblesJoinedName()
    {
        // Arrange
        await SeedMultiArtistPendingScrobbleAsync();

        // Act
        await _service.ProcessQueueAsync();

        // Assert
        await _scrobblerService.Received(1).ScrobbleAsync(
            Arg.Is<Song>(s => s.ArtistName == "Artist 1 & Artist 2"), 
            Arg.Any<DateTime>());
    }
}