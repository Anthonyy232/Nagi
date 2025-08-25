using FluentAssertions;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;
using Nagi.Core.Services.Implementations;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Nagi.Core.Tests;

/// <summary>
///     Provides comprehensive unit tests for the <see cref="MusicPlaybackService" />.
///     All external dependencies are mocked to ensure isolated testing of the service's logic.
/// </summary>
public class MusicPlaybackServiceTests
{
    private readonly IAudioPlayer _audioPlayer;
    private readonly ILibraryService _libraryService;
    private readonly IMetadataService _metadataService;

    /// <summary>
    ///     The instance of the service under test.
    /// </summary>
    private readonly MusicPlaybackService _service;

    // Mocks for external dependencies, enabling isolated testing of the service's logic.
    private readonly ISettingsService _settingsService;

    // A consistent list of song objects for use in tests.
    private readonly List<Song> _testSongs;

    /// <summary>
    ///     Initializes a new instance of the <see cref="MusicPlaybackServiceTests" /> class.
    ///     This constructor sets up the required mocks and instantiates the
    ///     <see cref="MusicPlaybackService" /> with these test dependencies.
    /// </summary>
    public MusicPlaybackServiceTests()
    {
        _settingsService = Substitute.For<ISettingsService>();
        _audioPlayer = Substitute.For<IAudioPlayer>();
        _libraryService = Substitute.For<ILibraryService>();
        _metadataService = Substitute.For<IMetadataService>();

        // Setup default return values for settings to avoid nulls
        _settingsService.GetInitialVolumeAsync().Returns(0.5);
        _settingsService.GetInitialMuteStateAsync().Returns(false);
        _settingsService.GetInitialShuffleStateAsync().Returns(false);
        _settingsService.GetInitialRepeatModeAsync().Returns(RepeatMode.Off);
        _settingsService.GetRestorePlaybackStateEnabledAsync().Returns(false);
        _settingsService.GetPlaybackStateAsync().Returns((PlaybackState?)null);
        _settingsService.GetEqualizerSettingsAsync().Returns((EqualizerSettings?)null);

        // Setup audio player with some default properties
        _audioPlayer.GetEqualizerBands().Returns(new List<(uint, float)> { (0, 60f), (1, 170f) });

        _service = new MusicPlaybackService(
            _settingsService,
            _audioPlayer,
            _libraryService,
            _metadataService);

        _testSongs = CreateTestSongs(5);
    }

    /// <summary>
    ///     Helper to create a list of unique song objects for testing.
    /// </summary>
    private static List<Song> CreateTestSongs(int count)
    {
        return Enumerable.Range(1, count)
            .Select(i => new Song { Id = Guid.NewGuid(), Title = $"Song {i}", FilePath = $"C:\\music\\song{i}.mp3" })
            .ToList();
    }

    #region Transient Playback Tests

    /// <summary>
    ///     Verifies that PlayTransientFileAsync correctly plays a file not in the library,
    ///     clearing the existing queue and creating a temporary Song object.
    /// </summary>
    [Fact]
    public async Task PlayTransientFileAsync_WhenCalled_ClearsQueueAndPlaysFile()
    {
        // Arrange
        const string filePath = "C:\\temp\\transient.mp3";
        var metadata = new SongFileMetadata { Title = "Transient Song", Artist = "Temp Artist" };
        _metadataService.ExtractMetadataAsync(filePath).Returns(metadata);
        await _service.InitializeAsync();
        await _service.PlayAsync(_testSongs); // Pre-load a queue
        _audioPlayer.ClearReceivedCalls();

        // Act
        await _service.PlayTransientFileAsync(filePath);

        // Assert
        _service.PlaybackQueue.Should().BeEmpty();
        _service.CurrentTrack.Should().NotBeNull();
        _service.CurrentTrack!.Title.Should().Be("Transient Song");
        _service.CurrentTrack!.Artist!.Name.Should().Be("Temp Artist");
        _service.CurrentQueueIndex.Should().Be(-1);
        await _audioPlayer.Received(1).LoadAsync(Arg.Is<Song>(s => s.FilePath == filePath));
        await _audioPlayer.Received(1).PlayAsync();
    }

    #endregion

    /// <summary>
    ///     Helper class to track event invocations for verification.
    /// </summary>
    private class EventTracker
    {
        public EventTracker(MusicPlaybackService service)
        {
            service.ShuffleModeChanged += () => ShuffleModeChangedCount++;
            service.RepeatModeChanged += () => RepeatModeChangedCount++;
            service.QueueChanged += () => QueueChangedCount++;
            service.EqualizerChanged += () => EqualizerChangedCount++;
        }

        public int ShuffleModeChangedCount { get; private set; }
        public int RepeatModeChangedCount { get; private set; }
        public int QueueChangedCount { get; private set; }
        public int EqualizerChangedCount { get; private set; }
    }

    #region Initialization Tests

    /// <summary>
    ///     Verifies that <see cref="MusicPlaybackService.InitializeAsync" /> correctly loads initial
    ///     settings (volume, mute, shuffle, repeat) from the settings service and applies them to
    ///     the audio player and service state.
    /// </summary>
    [Fact]
    public async Task InitializeAsync_WhenCalledFirstTime_LoadsSettingsAndAppliesThem()
    {
        // Arrange
        _settingsService.GetInitialVolumeAsync().Returns(0.75);
        _settingsService.GetInitialMuteStateAsync().Returns(true);
        _settingsService.GetInitialShuffleStateAsync().Returns(true);
        _settingsService.GetInitialRepeatModeAsync().Returns(RepeatMode.RepeatAll);

        // Act
        await _service.InitializeAsync();

        // Assert
        await _audioPlayer.Received(1).SetVolumeAsync(0.75);
        await _audioPlayer.Received(1).SetMuteAsync(true);
        _service.IsShuffleEnabled.Should().BeTrue();
        _service.CurrentRepeatMode.Should().Be(RepeatMode.RepeatAll);
    }

    /// <summary>
    ///     Verifies that if an exception occurs during initialization, the service gracefully
    ///     falls back to default playback settings without crashing.
    /// </summary>
    [Fact]
    public async Task InitializeAsync_WhenSettingsServiceThrows_FallsBackToDefaultSettings()
    {
        // Arrange
        _settingsService.GetInitialVolumeAsync().ThrowsAsync(new InvalidOperationException("Config file corrupted"));

        // Act
        await _service.InitializeAsync();

        // Assert
        await _audioPlayer.Received(1).SetVolumeAsync(0.5);
        await _audioPlayer.Received(1).SetMuteAsync(false);
        _service.IsShuffleEnabled.Should().BeFalse();
        _service.CurrentRepeatMode.Should().Be(RepeatMode.Off);
        _service.PlaybackQueue.Should().BeEmpty();
    }

    /// <summary>
    ///     Verifies that when session restore is enabled, <see cref="MusicPlaybackService.InitializeAsync" />
    ///     successfully restores the previous playback queue, current track, and position.
    /// </summary>
    [Fact]
    public async Task InitializeAsync_WithSessionRestoreEnabled_RestoresStateSuccessfully()
    {
        // Arrange
        var songIds = _testSongs.Select(s => s.Id).ToList();
        var savedState = new PlaybackState
        {
            CurrentTrackId = _testSongs[1].Id,
            CurrentPositionSeconds = 30,
            PlaybackQueueTrackIds = songIds,
            CurrentPlaybackQueueIndex = 1
        };
        _settingsService.GetRestorePlaybackStateEnabledAsync().Returns(true);
        _settingsService.GetPlaybackStateAsync().Returns(savedState);
        _libraryService.GetSongsByIdsAsync(Arg.Is<IEnumerable<Guid>>(ids => ids.SequenceEqual(songIds)))
            .Returns(_testSongs.ToDictionary(s => s.Id));

        // Configure the mock to raise DurationChanged after LoadAsync is called.
        // This is required for the SeekAsync logic in RestoreInternalPlaybackStateAsync to execute.
        _audioPlayer.When(x => x.LoadAsync(Arg.Any<Song>())).Do(x =>
        {
            _audioPlayer.Duration.Returns(TimeSpan.FromMinutes(3));
            _audioPlayer.DurationChanged += Raise.Event<Action>();
        });

        // Act
        await _service.InitializeAsync();

        // Assert
        _service.PlaybackQueue.Should().BeEquivalentTo(_testSongs);
        _service.CurrentTrack.Should().Be(_testSongs[1]);
        _service.CurrentQueueIndex.Should().Be(1);
        await _audioPlayer.Received(1).LoadAsync(_testSongs[1]);
        await _audioPlayer.Received(1).SeekAsync(TimeSpan.FromSeconds(30));
    }

    /// <summary>
    ///     Verifies that <see cref="MusicPlaybackService.InitializeAsync" /> is idempotent and only
    ///     performs the initialization logic on the first call.
    /// </summary>
    [Fact]
    public async Task InitializeAsync_WhenCalledMultipleTimes_OnlyInitializesOnce()
    {
        // Act
        await _service.InitializeAsync();
        await _service.InitializeAsync();

        // Assert
        await _settingsService.Received(1).GetInitialVolumeAsync();
        await _audioPlayer.Received(1).SetVolumeAsync(Arg.Any<double>());
    }

    /// <summary>
    ///     Verifies that if session restore is enabled but the songs from the saved state
    ///     cannot be found in the library, the service gracefully clears the queue.
    /// </summary>
    [Fact]
    public async Task InitializeAsync_WithSessionRestoreAndMissingSongs_ClearsQueue()
    {
        // Arrange
        var savedState = new PlaybackState
        {
            PlaybackQueueTrackIds = _testSongs.Select(s => s.Id).ToList(),
            CurrentTrackId = _testSongs[1].Id
        };
        _settingsService.GetRestorePlaybackStateEnabledAsync().Returns(true);
        _settingsService.GetPlaybackStateAsync().Returns(savedState);
        // Simulate songs not being found in the library
        _libraryService.GetSongsByIdsAsync(Arg.Any<IEnumerable<Guid>>())
            .Returns(new Dictionary<Guid, Song>());

        // Act
        await _service.InitializeAsync();

        // Assert
        _service.PlaybackQueue.Should().BeEmpty();
        _service.CurrentTrack.Should().BeNull();
        await _audioPlayer.DidNotReceive().LoadAsync(Arg.Any<Song>());
    }

    #endregion

    #region Playback Control Tests

    /// <summary>
    ///     Verifies that <see cref="MusicPlaybackService.PlayAsync(Song)" /> creates a new queue
    ///     with the specified song, sets it as the current track, and starts playback.
    /// </summary>
    [Fact]
    public async Task PlayAsync_WithSingleSong_CreatesQueueAndPlaysSong()
    {
        // Arrange
        var song = _testSongs[0];
        await _service.InitializeAsync();

        // Act
        await _service.PlayAsync(song);

        // Assert
        _service.PlaybackQueue.Should().HaveCount(1).And.Contain(song);
        _service.CurrentTrack.Should().Be(song);
        _service.CurrentQueueIndex.Should().Be(0);
        await _audioPlayer.Received(1).LoadAsync(song);
        await _audioPlayer.Received(1).PlayAsync();
    }

    /// <summary>
    ///     Verifies that <see cref="MusicPlaybackService.PlayAsync(IEnumerable{Song}, int)" />
    ///     replaces the existing queue with the new list and starts playback from the specified index.
    /// </summary>
    [Fact]
    public async Task PlayAsync_WithSongList_ReplacesQueueAndPlaysFromStartIndex()
    {
        // Arrange
        await _service.InitializeAsync();

        // Act
        await _service.PlayAsync(_testSongs, 2);

        // Assert
        _service.PlaybackQueue.Should().BeEquivalentTo(_testSongs, options => options.WithStrictOrdering());
        _service.CurrentTrack.Should().Be(_testSongs[2]);
        _service.CurrentQueueIndex.Should().Be(2);
        await _audioPlayer.Received(1).LoadAsync(_testSongs[2]);
    }

    /// <summary>
    ///     Verifies that calling PlayAsync with an empty list correctly stops playback
    ///     and clears all queues, preventing any potential errors.
    /// </summary>
    [Fact]
    public async Task PlayAsync_WithEmptyList_StopsAndClearsQueue()
    {
        // Arrange
        await _service.InitializeAsync();
        await _service.PlayAsync(_testSongs); // Pre-load a queue

        // Act
        await _service.PlayAsync(new List<Song>());

        // Assert
        await _audioPlayer.Received(1).StopAsync();
        _service.PlaybackQueue.Should().BeEmpty();
        _service.CurrentTrack.Should().BeNull();
    }

    /// <summary>
    ///     Verifies that PlayAsync correctly handles an out-of-bounds start index by defaulting to 0.
    /// </summary>
    [Fact]
    public async Task PlayAsync_WithInvalidStartIndex_PlaysFromBeginning()
    {
        // Arrange
        await _service.InitializeAsync();

        // Act
        await _service.PlayAsync(_testSongs, 99);

        // Assert
        _service.CurrentTrack.Should().Be(_testSongs[0]);
        _service.CurrentQueueIndex.Should().Be(0);
        await _audioPlayer.Received(1).LoadAsync(_testSongs[0]);
    }

    /// <summary>
    ///     Verifies that <see cref="MusicPlaybackService.PlayPauseAsync" /> correctly pauses
    ///     playback when the audio player is currently playing.
    /// </summary>
    [Fact]
    public async Task PlayPauseAsync_WhenPlaying_PausesPlayback()
    {
        // Arrange
        await _service.InitializeAsync();
        await _service.PlayAsync(_testSongs[0]);
        _audioPlayer.IsPlaying.Returns(true);

        // Act
        await _service.PlayPauseAsync();

        // Assert
        await _audioPlayer.Received(1).PauseAsync();
    }

    /// <summary>
    ///     Verifies that <see cref="MusicPlaybackService.PlayPauseAsync" /> correctly resumes
    ///     playback when the audio player is currently paused.
    /// </summary>
    [Fact]
    public async Task PlayPauseAsync_WhenPaused_ResumesPlayback()
    {
        // Arrange
        await _service.InitializeAsync();
        await _service.PlayAsync(_testSongs[0]);
        _audioPlayer.IsPlaying.Returns(false); // Simulate paused state

        // Act
        await _service.PlayPauseAsync();

        // Assert
        await _audioPlayer.Received(2).PlayAsync(); // 1 from PlayAsync, 1 from PlayPauseAsync
    }

    /// <summary>
    ///     Verifies that if PlayPause is called when no track is active but a queue exists
    ///     (e.g., after StopAsync), it resumes playback from the last known position in the queue.
    /// </summary>
    [Fact]
    public async Task PlayPauseAsync_WhenStoppedWithQueue_PlaysFromCurrentIndex()
    {
        // Arrange
        await _service.InitializeAsync();
        await _service.PlayAsync(_testSongs, 2);
        await _service.StopAsync(); // CurrentTrack is now null, but index is 2
        _audioPlayer.IsPlaying.Returns(false);
        _audioPlayer.ClearReceivedCalls();

        // Act
        await _service.PlayPauseAsync();

        // Assert
        _service.CurrentTrack.Should().Be(_testSongs[2]);
        await _audioPlayer.Received(1).LoadAsync(_testSongs[2]);
    }

    /// <summary>
    ///     Verifies that <see cref="MusicPlaybackService.NextAsync" /> advances to the next song
    ///     in the queue in normal playback mode.
    /// </summary>
    [Fact]
    public async Task NextAsync_InNormalMode_PlaysNextSong()
    {
        // Arrange
        await _service.InitializeAsync();
        await _service.PlayAsync(_testSongs, 1);

        // Act
        await _service.NextAsync();

        // Assert
        _service.CurrentTrack.Should().Be(_testSongs[2]);
        await _audioPlayer.Received(1).LoadAsync(_testSongs[2]);
    }

    /// <summary>
    ///     Verifies that NextAsync with RepeatOne mode enabled simply restarts the current track.
    /// </summary>
    [Fact]
    public async Task NextAsync_WithRepeatOne_RestartsCurrentTrack()
    {
        // Arrange
        await _service.InitializeAsync();
        await _service.SetRepeatModeAsync(RepeatMode.RepeatOne);
        await _service.PlayAsync(_testSongs, 1);
        _audioPlayer.ClearReceivedCalls();

        // Act
        await _service.NextAsync();

        // Assert
        _service.CurrentTrack.Should().Be(_testSongs[1]);
        await _audioPlayer.Received(1).LoadAsync(_testSongs[1]);
        await _audioPlayer.Received(1).PlayAsync();
    }

    /// <summary>
    ///     Verifies that when <see cref="MusicPlaybackService.NextAsync" /> is called on the last
    ///     song of the queue with RepeatAll enabled, it wraps around and plays the first song.
    /// </summary>
    [Fact]
    public async Task NextAsync_AtEndOfQueueWithRepeatAll_PlaysFirstSong()
    {
        // Arrange
        await _service.InitializeAsync();
        await _service.SetRepeatModeAsync(RepeatMode.RepeatAll);
        await _service.PlayAsync(_testSongs, _testSongs.Count - 1);

        // Act
        await _service.NextAsync();

        // Assert
        _service.CurrentTrack.Should().Be(_testSongs[0]);
        await _audioPlayer.Received(1).LoadAsync(_testSongs[0]);
    }

    /// <summary>
    ///     Verifies that when <see cref="MusicPlaybackService.NextAsync" /> is called on the last
    ///     song of the queue with no repeat mode enabled, it stops playback.
    /// </summary>
    [Fact]
    public async Task NextAsync_AtEndOfQueueWithNoRepeat_StopsPlayback()
    {
        // Arrange
        await _service.InitializeAsync();
        await _service.PlayAsync(_testSongs, _testSongs.Count - 1);

        // Act
        await _service.NextAsync();

        // Assert
        _service.CurrentTrack.Should().BeNull();
        await _audioPlayer.Received(1).StopAsync();
    }

    /// <summary>
    ///     Verifies that <see cref="MusicPlaybackService.PreviousAsync" /> restarts the current
    ///     track if the playback position is greater than three seconds.
    /// </summary>
    [Fact]
    public async Task PreviousAsync_WhenPositionIsOver3Seconds_RestartsTrack()
    {
        // Arrange
        await _service.InitializeAsync();
        await _service.PlayAsync(_testSongs, 2);
        _audioPlayer.CurrentPosition.Returns(TimeSpan.FromSeconds(5));

        // Act
        await _service.PreviousAsync();

        // Assert
        await _audioPlayer.Received(1).SeekAsync(TimeSpan.Zero);
        _service.CurrentTrack.Should().Be(_testSongs[2]);
    }

    /// <summary>
    ///     Verifies that <see cref="MusicPlaybackService.PreviousAsync" /> plays the previous
    ///     track in the queue if the playback position is less than three seconds.
    /// </summary>
    [Fact]
    public async Task PreviousAsync_WhenPositionIsUnder3Seconds_PlaysPreviousTrack()
    {
        // Arrange
        await _service.InitializeAsync();
        await _service.PlayAsync(_testSongs, 2);
        _audioPlayer.CurrentPosition.Returns(TimeSpan.FromSeconds(2));

        // Act
        await _service.PreviousAsync();

        // Assert
        _service.CurrentTrack.Should().Be(_testSongs[1]);
        await _audioPlayer.Received(1).LoadAsync(_testSongs[1]);
    }

    /// <summary>
    ///     Verifies that PreviousAsync with RepeatOne mode enabled simply restarts the current track,
    ///     regardless of the current playback position.
    /// </summary>
    [Fact]
    public async Task PreviousAsync_WithRepeatOne_RestartsCurrentTrack()
    {
        // Arrange
        await _service.InitializeAsync();
        await _service.SetRepeatModeAsync(RepeatMode.RepeatOne);
        await _service.PlayAsync(_testSongs, 1);
        _audioPlayer.CurrentPosition.Returns(TimeSpan.FromSeconds(10)); // Position > 3s
        _audioPlayer.ClearReceivedCalls();

        // Act
        await _service.PreviousAsync();

        // Assert
        _service.CurrentTrack.Should().Be(_testSongs[1]);
        await _audioPlayer.Received(1).LoadAsync(_testSongs[1]);
        await _audioPlayer.Received(1).PlayAsync();
    }

    /// <summary>
    ///     Verifies that calling PreviousAsync at the start of the queue with RepeatAll enabled
    ///     correctly wraps around to the last song.
    /// </summary>
    [Fact]
    public async Task PreviousAsync_AtStartOfQueueWithRepeatAll_PlaysLastSong()
    {
        // Arrange
        await _service.InitializeAsync();
        await _service.SetRepeatModeAsync(RepeatMode.RepeatAll);
        await _service.PlayAsync(_testSongs, 0);
        _audioPlayer.CurrentPosition.Returns(TimeSpan.FromSeconds(1)); // Position < 3s

        // Act
        await _service.PreviousAsync();

        // Assert
        _service.CurrentTrack.Should().Be(_testSongs.Last());
        await _audioPlayer.Received(1).LoadAsync(_testSongs.Last());
    }

    #endregion

    #region Shuffle Mode Interaction Tests

    /// <summary>
    ///     Verifies that enabling shuffle mode creates a shuffled version of the playback queue,
    ///     saves the state, and raises the appropriate events.
    /// </summary>
    [Fact]
    public async Task SetShuffleAsync_WhenEnabled_GeneratesShuffledQueue()
    {
        // Arrange
        await _service.InitializeAsync();
        await _service.PlayAsync(_testSongs);
        var eventTracker = new EventTracker(_service);

        // Act
        await _service.SetShuffleAsync(true);

        // Assert
        _service.IsShuffleEnabled.Should().BeTrue();
        _service.ShuffledQueue.Should().HaveCount(_testSongs.Count);
        _service.ShuffledQueue.Should().BeEquivalentTo(_testSongs);
        _service.ShuffledQueue.Should().NotBeEquivalentTo(_testSongs, opt => opt.WithStrictOrdering());
        await _settingsService.Received(1).SaveShuffleStateAsync(true);
        eventTracker.ShuffleModeChangedCount.Should().Be(1);
        eventTracker.QueueChangedCount.Should().Be(1);
    }

    /// <summary>
    ///     Verifies that disabling shuffle mode correctly clears the shuffled queue and
    ///     updates the service state.
    /// </summary>
    [Fact]
    public async Task SetShuffleAsync_WhenDisabled_ClearsShuffledQueue()
    {
        // Arrange
        await _service.InitializeAsync();
        await _service.PlayAsync(_testSongs);
        await _service.SetShuffleAsync(true); // First, enable it
        _service.ShuffledQueue.Should().NotBeEmpty();

        // Act
        await _service.SetShuffleAsync(false);

        // Assert
        _service.IsShuffleEnabled.Should().BeFalse();
        _service.ShuffledQueue.Should().BeEmpty();
        await _settingsService.Received(1).SaveShuffleStateAsync(false);
    }

    /// <summary>
    ///     Verifies that when shuffle is enabled, NextAsync plays the next song from the
    ///     shuffled queue, not the original playback queue.
    /// </summary>
    [Fact]
    public async Task NextAsync_WithShuffleEnabled_PlaysNextSongInShuffledQueue()
    {
        // Arrange
        await _service.InitializeAsync();
        await _service.PlayAsync(_testSongs);
        await _service.SetShuffleAsync(true);

        // Manually set the current track to the first in the shuffled queue to have a known start point
        var firstShuffledSong = _service.ShuffledQueue[0];
        var firstShuffledSongOriginalIndex = _testSongs.IndexOf(firstShuffledSong);
        await _service.PlayQueueItemAsync(firstShuffledSongOriginalIndex);

        var expectedNextSong = _service.ShuffledQueue[1];
        _audioPlayer.ClearReceivedCalls();

        // Act
        await _service.NextAsync();

        // Assert
        _service.CurrentTrack.Should().Be(expectedNextSong);
        await _audioPlayer.Received(1).LoadAsync(expectedNextSong);
    }

    /// <summary>
    ///     Verifies that toggling shuffle mode on preserves the currently playing track.
    /// </summary>
    [Fact]
    public async Task SetShuffleAsync_WhenEnabled_MaintainsCurrentTrack()
    {
        // Arrange
        await _service.InitializeAsync();
        await _service.PlayAsync(_testSongs, 2); // Playing _testSongs[2]
        var currentTrackBeforeShuffle = _service.CurrentTrack;

        // Act
        await _service.SetShuffleAsync(true);

        // Assert
        _service.IsShuffleEnabled.Should().BeTrue();
        _service.CurrentTrack.Should().Be(currentTrackBeforeShuffle);
        _service.ShuffledQueue.Should().Contain(currentTrackBeforeShuffle!);
    }

    #endregion

    #region Queue Management Tests

    /// <summary>
    ///     Verifies that <see cref="MusicPlaybackService.AddToQueueAsync" /> adds a song to the
    ///     end of the main playback queue.
    /// </summary>
    [Fact]
    public async Task AddToQueueAsync_WhenCalled_AddsSongToEndOfQueue()
    {
        // Arrange
        await _service.InitializeAsync();
        await _service.PlayAsync(_testSongs.Take(2).ToList());
        var songToAdd = _testSongs[3];

        // Act
        await _service.AddToQueueAsync(songToAdd);

        // Assert
        _service.PlaybackQueue.Should().HaveCount(3);
        _service.PlaybackQueue.Last().Should().Be(songToAdd);
    }

    /// <summary>
    ///     Verifies that adding a song to the queue while shuffle is active correctly
    ///     regenerates the shuffled queue to include the new song.
    /// </summary>
    [Fact]
    public async Task AddToQueueAsync_WithShuffleEnabled_AddsToBothQueues()
    {
        // Arrange
        await _service.InitializeAsync();
        await _service.PlayAsync(_testSongs);
        await _service.SetShuffleAsync(true);
        var songToAdd = new Song { Id = Guid.NewGuid(), Title = "New Song" };

        // Act
        await _service.AddToQueueAsync(songToAdd);

        // Assert
        _service.PlaybackQueue.Should().Contain(songToAdd);
        _service.ShuffledQueue.Should().Contain(songToAdd);
        _service.PlaybackQueue.Count.Should().Be(_testSongs.Count + 1);
        _service.ShuffledQueue.Count.Should().Be(_testSongs.Count + 1);
    }

    /// <summary>
    ///     Verifies that <see cref="MusicPlaybackService.PlayNextAsync" /> correctly inserts a
    ///     song into the queue immediately after the currently playing track.
    /// </summary>
    [Fact]
    public async Task PlayNextAsync_WhenCalled_InsertsSongAfterCurrentTrack()
    {
        // Arrange
        await _service.InitializeAsync();
        await _service.PlayAsync(_testSongs, 1); // Playing Song 2
        var songToPlayNext = new Song { Id = Guid.NewGuid(), Title = "Next Song" };

        // Act
        await _service.PlayNextAsync(songToPlayNext);

        // Assert
        _service.PlaybackQueue.Should().HaveCount(_testSongs.Count + 1);
        _service.PlaybackQueue[2].Should().Be(songToPlayNext); // Index 0: S1, 1: S2, 2: Next, 3: S3...
    }

    /// <summary>
    ///     Verifies that if PlayNextAsync is called with a song already in the queue,
    ///     it is moved to the next position instead of being duplicated.
    /// </summary>
    [Fact]
    public async Task PlayNextAsync_WithSongAlreadyInQueue_MovesSongToNextPosition()
    {
        // Arrange
        await _service.InitializeAsync();
        await _service.PlayAsync(_testSongs, 0); // Playing Song 1
        var songToMove = _testSongs[3]; // Song 4

        // Act
        await _service.PlayNextAsync(songToMove);

        // Assert
        _service.PlaybackQueue.Should().HaveCount(_testSongs.Count); // Count should not change
        _service.PlaybackQueue[1].Should().Be(songToMove); // Song 4 should now be at index 1
    }

    /// <summary>
    ///     Verifies that removing the currently playing song from the queue causes playback to
    ///     advance to the next available track.
    /// </summary>
    [Fact]
    public async Task RemoveFromQueueAsync_WhenRemovingCurrentTrack_PlaysNextTrack()
    {
        // Arrange
        await _service.InitializeAsync();
        await _service.PlayAsync(_testSongs, 1); // Playing Song 2
        var songToRemove = _testSongs[1];

        // Act
        await _service.RemoveFromQueueAsync(songToRemove);

        // Assert
        _service.PlaybackQueue.Should().NotContain(songToRemove);
        _service.CurrentTrack.Should().Be(_testSongs[2]); // Should have advanced to the next song
        await _audioPlayer.Received(1).StopAsync();
        await _audioPlayer.Received(1).LoadAsync(_testSongs[2]);
    }

    /// <summary>
    ///     Verifies that removing a song that appeared before the current track correctly
    ///     decrements the CurrentQueueIndex to maintain the correct position.
    /// </summary>
    [Fact]
    public async Task RemoveFromQueueAsync_WhenRemovingTrackBeforeCurrent_AdjustsCurrentIndex()
    {
        // Arrange
        await _service.InitializeAsync();
        await _service.PlayAsync(_testSongs, 2); // Playing Song 3 (index 2)
        var songToRemove = _testSongs[0]; // Removing Song 1 (index 0)

        // Act
        await _service.RemoveFromQueueAsync(songToRemove);

        // Assert
        _service.PlaybackQueue.Should().NotContain(songToRemove);
        _service.CurrentTrack.Should().Be(_testSongs[2]); // Still playing Song 3
        _service.CurrentQueueIndex.Should().Be(1); // Index should now be 1
    }

    /// <summary>
    ///     Verifies that removing a song that is not the current track updates the queue
    ///     without interrupting playback.
    /// </summary>
    [Fact]
    public async Task RemoveFromQueueAsync_WhenRemovingOtherTrack_QueueIsUpdated()
    {
        // Arrange
        await _service.InitializeAsync();
        await _service.PlayAsync(_testSongs, 1); // Playing Song 2
        var songToRemove = _testSongs[3];

        // Act
        await _service.RemoveFromQueueAsync(songToRemove);

        // Assert
        _service.PlaybackQueue.Should().NotContain(songToRemove);
        _service.CurrentTrack.Should().Be(_testSongs[1]); // Current track should be unaffected
        await _audioPlayer.DidNotReceive().StopAsync();
    }

    /// <summary>
    ///     Verifies that removing the last playing track from a non-repeating queue stops playback.
    /// </summary>
    [Fact]
    public async Task RemoveFromQueueAsync_WhenRemovingLastPlayingTrack_StopsPlayback()
    {
        // Arrange
        var singleSongList = new List<Song> { _testSongs[0] };
        await _service.InitializeAsync();
        await _service.PlayAsync(singleSongList);
        _service.CurrentRepeatMode.Should().Be(RepeatMode.Off);

        // Act
        await _service.RemoveFromQueueAsync(_testSongs[0]);

        // Assert
        _service.PlaybackQueue.Should().BeEmpty();
        _service.CurrentTrack.Should().BeNull();
        await _audioPlayer.Received(2).StopAsync(); // Once from Remove, once from StopAsync call inside
    }

    /// <summary>
    ///     Verifies that <see cref="MusicPlaybackService.ClearQueueAsync" /> stops playback and
    ///     removes all songs from both the main and shuffled queues.
    /// </summary>
    [Fact]
    public async Task ClearQueueAsync_WhenCalled_StopsPlaybackAndClearsQueues()
    {
        // Arrange
        await _service.InitializeAsync();
        await _service.PlayAsync(_testSongs);

        // Act
        await _service.ClearQueueAsync();

        // Assert
        _service.PlaybackQueue.Should().BeEmpty();
        _service.ShuffledQueue.Should().BeEmpty();
        _service.CurrentTrack.Should().BeNull();
        _service.CurrentQueueIndex.Should().Be(-1);
        await _audioPlayer.Received(1).StopAsync();
    }

    #endregion

    #region Mode and State Tests

    /// <summary>
    ///     Verifies that <see cref="MusicPlaybackService.SetRepeatModeAsync" /> updates the repeat
    ///     mode property, saves the new setting, and raises the corresponding event.
    /// </summary>
    [Fact]
    public async Task SetRepeatModeAsync_WhenChanged_UpdatesPropertyAndSaves()
    {
        // Arrange
        await _service.InitializeAsync();
        var eventTracker = new EventTracker(_service);

        // Act
        await _service.SetRepeatModeAsync(RepeatMode.RepeatOne);

        // Assert
        _service.CurrentRepeatMode.Should().Be(RepeatMode.RepeatOne);
        await _settingsService.Received(1).SaveRepeatModeAsync(RepeatMode.RepeatOne);
        eventTracker.RepeatModeChangedCount.Should().Be(1);
    }

    /// <summary>
    ///     Verifies that <see cref="MusicPlaybackService.SavePlaybackStateAsync" /> correctly
    ///     captures the current queue, track, and position and passes it to the settings service
    ///     for persistence.
    /// </summary>
    [Fact]
    public async Task SavePlaybackStateAsync_WhenCalled_SavesCorrectState()
    {
        // Arrange
        await _service.InitializeAsync();
        await _service.PlayAsync(_testSongs, 2);
        _audioPlayer.CurrentPosition.Returns(TimeSpan.FromSeconds(45));

        // Act
        await _service.SavePlaybackStateAsync();

        // Assert
        await _settingsService.Received(1).SavePlaybackStateAsync(Arg.Is<PlaybackState>(state =>
            state.CurrentTrackId == _testSongs[2].Id &&
            state.CurrentPositionSeconds == 45 &&
            state.PlaybackQueueTrackIds.SequenceEqual(_testSongs.Select(s => s.Id)) &&
            state.CurrentPlaybackQueueIndex == 2
        ));
    }

    #endregion

    #region Equalizer Tests

    /// <summary>
    ///     Verifies that setting an equalizer band gain correctly updates the settings,
    ///     applies them to the audio player, and saves them.
    /// </summary>
    [Fact]
    public async Task SetEqualizerBandAsync_WithValidBand_AppliesAndSavesSettings()
    {
        // Arrange
        await _service.InitializeAsync();
        var eventTracker = new EventTracker(_service);

        // Act
        await _service.SetEqualizerBandAsync(1, 3.5f);

        // Assert
        _service.CurrentEqualizerSettings.Should().NotBeNull();
        _service.CurrentEqualizerSettings!.BandGains[1].Should().Be(3.5f);
        _audioPlayer.Received(2).ApplyEqualizerSettings(Arg.Is<EqualizerSettings>(s => s.BandGains[1] == 3.5f));
        await _settingsService.Received(1).SetEqualizerSettingsAsync(Arg.Any<EqualizerSettings>());
        eventTracker.EqualizerChangedCount.Should().Be(1);
    }

    /// <summary>
    ///     Verifies that setting the equalizer preamp gain correctly updates the settings,
    ///     applies them to the audio player, and saves them.
    /// </summary>
    [Fact]
    public async Task SetEqualizerPreampAsync_WhenCalled_AppliesAndSavesSettings()
    {
        // Arrange
        await _service.InitializeAsync();
        var eventTracker = new EventTracker(_service);

        // Act
        await _service.SetEqualizerPreampAsync(5.0f);

        // Assert
        _service.CurrentEqualizerSettings.Should().NotBeNull();
        _service.CurrentEqualizerSettings!.Preamp.Should().Be(5.0f);
        _audioPlayer.Received(2).ApplyEqualizerSettings(Arg.Is<EqualizerSettings>(s => s.Preamp == 5.0f));
        await _settingsService.Received(1).SetEqualizerSettingsAsync(Arg.Any<EqualizerSettings>());
        eventTracker.EqualizerChangedCount.Should().Be(1);
    }

    /// <summary>
    ///     Verifies that resetting the equalizer restores default values for preamp and all bands.
    /// </summary>
    [Fact]
    public async Task ResetEqualizerAsync_WhenCalled_ResetsAllGainsToDefault()
    {
        // Arrange
        await _service.InitializeAsync();
        await _service.SetEqualizerBandAsync(0, 5.0f);
        await _service.SetEqualizerPreampAsync(2.0f);
        _audioPlayer.ClearReceivedCalls();
        _settingsService.ClearReceivedCalls();

        // Act
        await _service.ResetEqualizerAsync();

        // Assert
        _service.CurrentEqualizerSettings!.Preamp.Should().Be(10.0f);
        _service.CurrentEqualizerSettings.BandGains.Should().AllSatisfy(g => g.Should().Be(0.0f));
        _audioPlayer.Received(1)
            .ApplyEqualizerSettings(
                Arg.Is<EqualizerSettings>(s => s.Preamp == 10.0f && s.BandGains.All(g => g == 0.0f)));
        await _settingsService.Received(1).SetEqualizerSettingsAsync(Arg.Any<EqualizerSettings>());
    }

    #endregion

    #region Audio Player Event Handling

    /// <summary>
    ///     Verifies that the service automatically advances to the next track when the audio
    ///     player's `PlaybackEnded` event is raised.
    /// </summary>
    [Fact]
    public async Task OnAudioPlayerPlaybackEnded_ShouldAdvanceToNextTrack()
    {
        // Arrange
        await _service.InitializeAsync();
        await _service.PlayAsync(_testSongs, 0);
        _audioPlayer.ClearReceivedCalls();

        // Act
        _audioPlayer.PlaybackEnded += Raise.Event<Action>();

        // Assert
        await Task.Delay(50); // Wait for async void event handler
        _service.CurrentTrack.Should().Be(_testSongs[1]);
        await _audioPlayer.Received(1).LoadAsync(_testSongs[1]);
    }

    /// <summary>
    ///     Verifies that when the last song in a non-repeating queue ends, playback stops.
    /// </summary>
    [Fact]
    public async Task OnAudioPlayerPlaybackEnded_AtEndOfQueueWithNoRepeat_StopsPlayback()
    {
        // Arrange
        await _service.InitializeAsync();
        await _service.PlayAsync(_testSongs, _testSongs.Count - 1); // Play the last song
        _service.CurrentRepeatMode.Should().Be(RepeatMode.Off);
        _audioPlayer.ClearReceivedCalls();

        // Act
        _audioPlayer.PlaybackEnded += Raise.Event<Action>();

        // Assert
        await Task.Delay(50); // Wait for async void event handler
        _service.CurrentTrack.Should().BeNull();
        await _audioPlayer.Received(1).StopAsync();
    }

    /// <summary>
    ///     Verifies that when the audio player reports an error, the service stops playback
    ///     to prevent further issues.
    /// </summary>
    [Fact]
    public async Task OnAudioPlayerErrorOccurred_ShouldStopPlayback()
    {
        // Arrange
        await _service.InitializeAsync();
        await _service.PlayAsync(_testSongs[0]);

        // Act
        _audioPlayer.ErrorOccurred += Raise.Event<Action<string>>("File not found");

        // Assert
        await Task.Delay(50); // Wait for async void event handler
        _service.CurrentTrack.Should().BeNull();
        await _audioPlayer.Received(1).StopAsync();
    }

    /// <summary>
    ///     Verifies that the service handles the System Media Transport Controls (SMTC) 'Next'
    ///     button press by calling the `NextAsync` method.
    /// </summary>
    [Fact]
    public async Task OnAudioPlayerSmtcNextButtonPressed_ShouldCallNextAsync()
    {
        // Arrange
        await _service.InitializeAsync();
        await _service.PlayAsync(_testSongs, 0);

        // Act
        _audioPlayer.SmtcNextButtonPressed += Raise.Event<Action>();

        // Assert
        await Task.Delay(50);
        _service.CurrentTrack.Should().Be(_testSongs[1]);
    }

    /// <summary>
    ///     Verifies that the service handles the System Media Transport Controls (SMTC) 'Previous'
    ///     button press by calling the `PreviousAsync` method.
    /// </summary>
    [Fact]
    public async Task OnAudioPlayerSmtcPreviousButtonPressed_ShouldCallPreviousAsync()
    {
        // Arrange
        await _service.InitializeAsync();
        await _service.PlayAsync(_testSongs, 1);
        _audioPlayer.CurrentPosition.Returns(TimeSpan.FromSeconds(1));

        // Act
        _audioPlayer.SmtcPreviousButtonPressed += Raise.Event<Action>();

        // Assert
        await Task.Delay(50);
        _service.CurrentTrack.Should().Be(_testSongs[0]);
    }

    #endregion

    #region Advanced Scenarios and Edge Cases

    /// <summary>
    ///     Verifies that calling NextAsync after playing a transient file (which has no queue)
    ///     correctly stops playback.
    /// </summary>
    [Fact]
    public async Task NextAsync_AfterPlayingTransientFile_StopsPlayback()
    {
        // Arrange
        const string filePath = "C:\\temp\\transient.mp3";
        _metadataService.ExtractMetadataAsync(filePath).Returns(new SongFileMetadata { Title = "Transient" });
        await _service.InitializeAsync();
        await _service.PlayTransientFileAsync(filePath);
        _service.PlaybackQueue.Should().BeEmpty();

        // Act
        await _service.NextAsync();

        // Assert
        _service.CurrentTrack.Should().BeNull();
        await _audioPlayer.Received(1).StopAsync();
    }

    /// <summary>
    ///     Verifies that if the queue contains only one song and Repeat All is on, NextAsync
    ///     just restarts that same song.
    /// </summary>
    [Fact]
    public async Task NextAsync_WithSingleSongAndRepeatAll_RestartsSong()
    {
        // Arrange
        var singleSongList = new List<Song> { _testSongs[0] };
        await _service.InitializeAsync();
        await _service.SetRepeatModeAsync(RepeatMode.RepeatAll);
        await _service.PlayAsync(singleSongList);
        _audioPlayer.ClearReceivedCalls();

        // Act
        await _service.NextAsync();

        // Assert
        _service.CurrentTrack.Should().Be(_testSongs[0]);
        await _audioPlayer.Received(1).LoadAsync(_testSongs[0]);
    }

    #endregion
}