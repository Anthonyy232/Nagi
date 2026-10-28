using FluentAssertions;
using Microsoft.Extensions.Logging;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;
using Nagi.Core.Services.Implementations;
using Nagi.Core.Tests.Utils;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Nagi.Core.Tests;

public partial class MusicPlaybackServiceTests : IAsyncDisposable
{
    private readonly IAudioPlayer _audioPlayer;
    private readonly LibraryFakes _library;
    private readonly ILogger<MusicPlaybackService> _logger;
    private readonly IMetadataService _metadataService;
    private readonly IFileSystemService _fileSystem = Substitute.For<IFileSystemService>();

    private readonly MusicPlaybackService _service;

    private readonly ISettingsService _settingsService;

    private readonly ISystemMediaControls _mediaControls = Substitute.For<ISystemMediaControls>();

    private readonly List<Song> _testSongs;

    public MusicPlaybackServiceTests()
    {
        _settingsService = Substitute.For<ISettingsService>();
        _audioPlayer = Substitute.For<IAudioPlayer>();
        _library = new LibraryFakes();
        _metadataService = Substitute.For<IMetadataService>();
        _logger = Substitute.For<ILogger<MusicPlaybackService>>();

        _settingsService.GetInitialVolumeAsync().Returns(0.5);
        _settingsService.GetInitialMuteStateAsync().Returns(false);
        _settingsService.GetInitialShuffleStateAsync().Returns(false);
        _settingsService.GetInitialRepeatModeAsync().Returns(RepeatMode.Off);
        _settingsService.GetRestorePlaybackStateEnabledAsync().Returns(false);
        _settingsService.GetPlaybackStateAsync().Returns((PlaybackState?)null);
        _settingsService.GetEqualizerSettingsAsync().Returns((EqualizerSettings?)null);

        _audioPlayer.GetEqualizerBands().Returns(new List<(uint, float)> { (0, 60f), (1, 170f) });

        _service = new MusicPlaybackService(
            _settingsService,
            _audioPlayer,
            _mediaControls,
            _library.Reader, _library.Writer,
            _metadataService,
            _fileSystem,
            _logger);

        _testSongs = CreateTestSongs(5);

        foreach (var song in _testSongs)
        {
            _library.Reader.GetSongByIdAsync(song.Id).Returns(song);
        }
    }

    public ValueTask DisposeAsync() => _service.DisposeAsync();

    private static List<Song> CreateTestSongs(int count)
    {
        return Enumerable.Range(1, count)
            .Select(i => new Song { Id = Guid.NewGuid(), Title = $"Song {i}", FilePath = $"C:\\music\\song{i}.mp3" })
            .ToList();
    }

    [Fact]
    public async Task AddRangeToQueueAsync_WithRepeatedTracks_AddsEachTrackOnce()
    {
        await _service.InitializeAsync();
        await _service.AddToQueueAsync(_testSongs[0]);
        await _service.AddToQueueAsync(_testSongs[1]);
        await _service.AddToQueueAsync(_testSongs[1]);
        await _service.AddRangeToQueueAsync(new[]
            { _testSongs[0].Id, _testSongs[1].Id, _testSongs[2].Id, _testSongs[2].Id, Guid.Empty });

        _service.PlaybackQueue.Should().Equal(_testSongs.Take(3).Select(song => song.Id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PlayAsync_WithDuplicateTracks_PreservesSelectedTrackAndUniqueQueue(bool shuffle)
    {
        await _service.InitializeAsync();
        await _service.SetShuffleAsync(shuffle);
        var first = _testSongs[0];
        var selected = _testSongs[1];

        await _service.PlayAsync(new[] { first.Id, Guid.Empty, first.Id, selected.Id }, startIndex: 3);

        _service.PlaybackQueue.Should().Equal(first.Id, selected.Id);
        _service.CurrentTrack.Should().BeSameAs(selected);
        _service.CurrentQueueIndex.Should().Be(1);
        if (shuffle) _service.ShuffledQueue.Should().BeEquivalentTo(new[] { first.Id, selected.Id });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MoveQueueItem_PreservesPlaybackAndSavedOrder(bool shuffle)
    {
        await _service.InitializeAsync();
        await _service.PlayAsync(_testSongs);
        await _service.SetShuffleAsync(shuffle);
        var current = _service.CurrentTrack!;
        var original = _service.PlaybackQueue.ToArray();
        var order = (shuffle ? _service.ShuffledQueue : _service.PlaybackQueue).ToList();
        var moved = order.Last(id => id != current.Id);
        order.Remove(moved);
        order.Insert(order.IndexOf(current.Id), moved);
        _audioPlayer.ClearReceivedCalls();

        _service.MoveQueueItem(moved, current.Id);

        (shuffle ? _service.ShuffledQueue : _service.PlaybackQueue).Should().Equal(order);
        _service.CurrentTrack.Should().BeSameAs(current);
        _service.PlaybackQueue[_service.CurrentQueueIndex].Should().Be(current.Id);
        if (shuffle)
        {
            _service.PlaybackQueue.Should().Equal(original);
            _service.ShuffledQueue[_service.CurrentShuffledIndex].Should().Be(current.Id);
        }
        await _audioPlayer.DidNotReceive().LoadAsync(Arg.Any<Song>());
        await _audioPlayer.DidNotReceive().StopAsync();
        PlaybackState? saved = null;
        _settingsService.SavePlaybackStateAsync(Arg.Do<PlaybackState>(s => saved = s)).Returns(Task.CompletedTask);
        await _service.SavePlaybackStateAsync();
        (shuffle ? saved!.ShuffledQueueTrackIds : saved!.PlaybackQueueTrackIds).Should().Equal(order);

        _service.MoveQueueItem(moved, null);
        (shuffle ? _service.ShuffledQueue : _service.PlaybackQueue).Last().Should().Be(moved);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemoveFromQueueAsync_AfterReorder_AdvancesInDisplayedOrder(bool shuffle)
    {
        await _service.InitializeAsync();
        await _service.PlayAsync(_testSongs);
        await _service.SetShuffleAsync(shuffle);
        var current = _service.CurrentTrack!.Id;
        var chosenNext = _testSongs[^1].Id;
        _service.MoveQueueItem(chosenNext, current);
        _service.MoveQueueItem(current, chosenNext);
        var expected = (shuffle ? _service.ShuffledQueue : _service.PlaybackQueue).Where(id => id != current).ToArray();

        await _service.RemoveFromQueueAsync(current);

        _service.CurrentTrack!.Id.Should().Be(chosenNext);
        (shuffle ? _service.ShuffledQueue : _service.PlaybackQueue).Should().Equal(expected);
    }

    [Fact]
    public async Task QueueMetadata_IncludesTransientGenresAndDurationWithoutDoubleCounting()
    {
        const string path = "C:\\temp\\queue.mp3";
        _metadataService.ExtractMetadataAsync(path).Returns(new SongFileMetadata
        {
            Title = "External",
            Genres = ["Rock", "rock", ""],
            Duration = TimeSpan.FromSeconds(90)
        });

        await _service.InitializeAsync();
        await _service.AddTransientFilesToQueueAsync([path]);
        var transientId = _service.PlaybackQueue.Single();
        var librarySong = _testSongs[0];
        _library.Reader.GetQueueSongsAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, Song> { [librarySong.Id] = librarySong });
        _library.Reader.GetSongsDurationAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(TimeSpan.FromSeconds(30));
        Guid[] ids = [transientId, librarySong.Id, transientId];

        var songs = await _service.GetQueueSongsAsync(ids);
        songs[transientId].Genres.Select(g => g.Name).Should().Equal("Rock");
        songs.Should().ContainKey(librarySong.Id);
        (await _service.GetQueueDurationAsync(ids)).Should().Be(TimeSpan.FromMinutes(2));
        await _library.Reader.Received().GetSongsDurationAsync(
            Arg.Is<IEnumerable<Guid>>(values => values.SequenceEqual(new[] { librarySong.Id })), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MoveQueueItem_WithLargeQueue_PreservesIdsAndIgnoresStaleTargets(bool shuffle)
    {
        await _service.InitializeAsync();
        var ids = new[] { _testSongs[0].Id }.Concat(Enumerable.Range(0, 50000).Select(_ => Guid.NewGuid())).ToArray();
        await _service.PlayAsync(ids);
        await _service.SetShuffleAsync(shuffle);
        var queue = shuffle ? _service.ShuffledQueue : _service.PlaybackQueue;
        var first = queue[0];
        var last = queue[^1];
        var expected = new[] { last }.Concat(queue.Take(queue.Count - 1)).ToArray();
        _service.MoveQueueItem(last, first);
        queue[0].Should().Be(last);
        _service.MoveQueueItem(last, Guid.NewGuid());
        _service.MoveQueueItem(Guid.NewGuid(), first);
        _service.MoveQueueItem(last, last);
        queue[0].Should().Be(last);
        queue.Should().Equal(expected);
        _service.PlaybackQueue[_service.CurrentQueueIndex].Should().Be(_testSongs[0].Id);
    }

    [Fact]
    public async Task PlayTransientFileAsync_WhenCalled_ClearsQueueAndPlaysFile()
    {
        const string filePath = "C:\\temp\\transient.mp3";
        var metadata = new SongFileMetadata
        {
            Title = "Transient Song",
            Artists = new List<string> { "Temp Artist" },
            Lyrics = "Embedded lyrics",
            LrcFilePath = "C:\\temp\\transient.lrc"
        };
        _metadataService.ExtractMetadataAsync(filePath).Returns(metadata);
        await _service.InitializeAsync();
        await _service.PlayAsync(_testSongs);
        _audioPlayer.ClearReceivedCalls();

        await _service.PlayTransientFileAsync(filePath);

        _service.PlaybackQueue.Should().BeEmpty();
        _service.CurrentTrack.Should().NotBeNull();
        _service.CurrentTrack!.Title.Should().Be("Transient Song");
        _service.CurrentTrack!.ArtistName.Should().Be("Temp Artist");
        _service.CurrentTrack!.Lyrics.Should().Be("Embedded lyrics");
        _service.CurrentTrack!.LrcFilePath.Should().Be("C:\\temp\\transient.lrc");
        _service.CurrentQueueIndex.Should().Be(-1);
        await _audioPlayer.Received(1).LoadAsync(Arg.Is<Song>(s => s != null && s.FilePath == filePath));
        await _audioPlayer.Received(1).PlayAsync();
    }

    [Fact]
    public async Task AddTransientFilesToQueueAsync_AppendsAndPlaysExternalFiles()
    {
        var firstPath = "C:\\temp\\first.mp3";
        var secondPath = "C:\\temp\\second.flac";
        _metadataService.ExtractMetadataAsync(firstPath).Returns(new SongFileMetadata { Title = "First" });
        _metadataService.ExtractMetadataAsync(secondPath).Returns(new SongFileMetadata { Title = "Second" });
        await _service.InitializeAsync();
        await _service.PlayAsync(_testSongs);
        _audioPlayer.ClearReceivedCalls();
        _library.ClearReceivedCalls();

        await _service.AddTransientFilesToQueueAsync([firstPath, secondPath]);

        _service.PlaybackQueue.Should().HaveCount(_testSongs.Count + 2);
        var externalIds = _service.PlaybackQueue.TakeLast(2).ToList();
        var tracks = await _service.GetQueueTracksAsync(externalIds);
        externalIds.Select(id => tracks[id].Title).Should().ContainInOrder("First", "Second");

        await _service.SavePlaybackStateAsync();
        await _settingsService.Received().SavePlaybackStateAsync(Arg.Is<PlaybackState>(state =>
            state.PlaybackQueueTrackIds.SequenceEqual(_testSongs.Select(song => song.Id))));

        await _service.PlayQueueItemAsync(_testSongs.Count);

        await _audioPlayer.Received(1).LoadAsync(Arg.Is<Song>(song => song.FilePath == firstPath));
        await _library.Writer.DidNotReceive().StartListenSessionAsync(
            externalIds[0], Arg.Any<PlaybackContext>());
    }

    [Fact]
    public async Task AddTransientFilesToQueueAsync_WhenStoppedWithAQueue_PlaysTheDroppedFile()
    {
        const string path = "C:\\temp\\dropped.mp3";
        _metadataService.ExtractMetadataAsync(path).Returns(new SongFileMetadata { Title = "Dropped" });
        await _service.InitializeAsync();
        await _service.PlayAsync(_testSongs);
        await _service.StopAsync();

        await _service.AddTransientFilesToQueueAsync([path]);

        _service.CurrentTrack!.FilePath.Should().Be(path);
    }

    [Fact]
    public async Task AddTransientFilesToQueueAsync_WithFolder_AddsItsAudioFilesInPathOrder()
    {
        const string folder = "C:\\drop";
        _fileSystem.DirectoryExists(folder).Returns(true);
        _fileSystem.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Returns(["C:\\drop\\b.mp3", "C:\\drop\\cover.jpg", "C:\\drop\\a\\c.flac"]);
        _metadataService.ExtractMetadataAsync(Arg.Any<string>())
            .Returns(call => new SongFileMetadata { Title = Path.GetFileName(call.ArgAt<string>(0)) });
        await _service.InitializeAsync();

        await _service.AddTransientFilesToQueueAsync([folder]);

        var tracks = await _service.GetQueueTracksAsync(_service.PlaybackQueue);
        _service.PlaybackQueue.Select(id => tracks[id].Title).Should().Equal("c.flac", "b.mp3");
    }

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

    [Fact]
    public async Task InitializeAsync_WithDuplicateSavedQueue_RestoresUniqueTracksInOriginalOrder()
    {
        var first = _testSongs[0];
        var second = _testSongs[1];
        _settingsService.GetRestorePlaybackStateEnabledAsync().Returns(true);
        _settingsService.GetPlaybackStateAsync().Returns(new PlaybackState
        {
            PlaybackQueueTrackIds = [first.Id, Guid.Empty, first.Id, second.Id],
            CurrentPlaybackQueueIndex = 3
        });

        await _service.InitializeAsync();

        _service.PlaybackQueue.Should().Equal(first.Id, second.Id);
        _service.CurrentQueueIndex.Should().Be(1);
        await _service.PlayPauseAsync();
        _service.CurrentTrack.Should().BeSameAs(second);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InitializeAsync_WithInvalidShuffleOfSameLength_RebuildsPermutation(bool duplicate)
    {
        var ids = _testSongs.Select(song => song.Id).ToList();
        var shuffle = ids.ToList();
        shuffle[^1] = duplicate ? ids[0] : Guid.NewGuid();
        _settingsService.GetRestorePlaybackStateEnabledAsync().Returns(true);
        _settingsService.GetInitialShuffleStateAsync().Returns(true);
        _settingsService.GetPlaybackStateAsync().Returns(new PlaybackState
        {
            PlaybackQueueTrackIds = ids,
            ShuffledQueueTrackIds = shuffle,
            CurrentTrackId = ids[0]
        });

        await _service.InitializeAsync();

        _service.ShuffledQueue.Should().OnlyHaveUniqueItems();
        _service.ShuffledQueue.Should().BeEquivalentTo(ids);
        _service.ShuffledQueue[_service.CurrentShuffledIndex].Should().Be(ids[0]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InitializeAsync_WithOutOfRangeSavedPosition_RemainsPlayable(bool shuffle)
    {
        var ids = _testSongs.Select(song => song.Id).ToList();
        _settingsService.GetRestorePlaybackStateEnabledAsync().Returns(true);
        _settingsService.GetInitialShuffleStateAsync().Returns(shuffle);
        _settingsService.GetPlaybackStateAsync().Returns(new PlaybackState
        {
            PlaybackQueueTrackIds = ids,
            ShuffledQueueTrackIds = ids.AsEnumerable().Reverse().ToList(),
            CurrentPlaybackQueueIndex = ids.Count,
            CurrentShuffledQueueIndex = ids.Count
        });

        await _service.InitializeAsync();
        await _service.PlayPauseAsync();

        _service.CurrentTrack.Should().NotBeNull();
        ids.Should().Contain(_service.CurrentTrack!.Id);
        _service.CurrentQueueIndex.Should().BeInRange(0, ids.Count - 1);
        if (shuffle) _service.CurrentShuffledIndex.Should().BeInRange(0, ids.Count - 1);
    }

    [Fact]
    public async Task InitializeAsync_WithCurrentTrackOutsideSavedQueue_DoesNotLoadUnqueuedTrack()
    {
        var first = _testSongs[0];
        var unqueued = _testSongs[1];
        _settingsService.GetRestorePlaybackStateEnabledAsync().Returns(true);
        _settingsService.GetPlaybackStateAsync().Returns(new PlaybackState
        {
            PlaybackQueueTrackIds = [first.Id],
            CurrentTrackId = unqueued.Id
        });

        await _service.InitializeAsync();

        _service.CurrentTrack.Should().BeNull();
        await _audioPlayer.DidNotReceive().LoadAsync(unqueued);
        await _service.PlayPauseAsync();
        _service.CurrentTrack.Should().BeSameAs(first);
    }

    [Fact]
    public async Task InitializeAsync_WhenCalledFirstTime_LoadsSettingsAndAppliesThem()
    {
        _settingsService.GetInitialVolumeAsync().Returns(0.75);
        _settingsService.GetInitialMuteStateAsync().Returns(true);
        _settingsService.GetInitialShuffleStateAsync().Returns(true);
        _settingsService.GetInitialRepeatModeAsync().Returns(RepeatMode.RepeatAll);

        await _service.InitializeAsync();

        await _audioPlayer.Received(1).SetVolumeAsync(0.75);
        await _audioPlayer.Received(1).SetMuteAsync(true);
        _service.IsShuffleEnabled.Should().BeTrue();
        _service.CurrentRepeatMode.Should().Be(RepeatMode.RepeatAll);
    }

    [Fact]
    public async Task InitializeAsync_WhenSettingsServiceThrows_FallsBackToDefaultSettings()
    {
        _settingsService.GetInitialVolumeAsync().ThrowsAsync(new InvalidOperationException("Config file corrupted"));
        _settingsService.GetInitialMuteStateAsync().Returns(true);
        _settingsService.GetInitialShuffleStateAsync().Returns(true);
        _settingsService.GetInitialRepeatModeAsync().Returns(RepeatMode.RepeatAll);

        await _service.InitializeAsync();

        await _audioPlayer.Received(1).SetVolumeAsync(0.5);
        await _audioPlayer.Received(1).SetMuteAsync(false);
        _service.IsShuffleEnabled.Should().BeFalse();
        _service.CurrentRepeatMode.Should().Be(RepeatMode.Off);
        _service.PlaybackQueue.Should().BeEmpty();
    }

    [Fact]
    public async Task InitializeAsync_WithSessionRestoreEnabled_RestoresStateSuccessfully()
    {
        var songIds = _testSongs.Select(s => s.Id).ToList();
        var savedState = new PlaybackState
        {
            CurrentTrackId = _testSongs[1].Id,
            PlaybackQueueTrackIds = songIds,
            CurrentPlaybackQueueIndex = 1
        };
        _settingsService.GetRestorePlaybackStateEnabledAsync().Returns(true);
        _settingsService.GetPlaybackStateAsync().Returns(savedState);
        await _service.InitializeAsync();

        _service.PlaybackQueue.Should().BeEquivalentTo(_testSongs.Select(s => s.Id));
        _service.CurrentTrack.Should().Be(_testSongs[1]);
        _service.CurrentQueueIndex.Should().Be(1);
        await _audioPlayer.Received(1).LoadAsync(_testSongs[1]);
        await _audioPlayer.DidNotReceive().SeekAsync(Arg.Any<TimeSpan>());
    }

    [Fact]
    public async Task InitializeAsync_WithSessionRestoreAndMissingCurrentTrack_ResetsToIndexZero()
    {
        var songIds = _testSongs.Select(s => s.Id).ToList();
        var savedState = new PlaybackState
        {
            PlaybackQueueTrackIds = songIds,
            CurrentTrackId = _testSongs[1].Id,
            CurrentPlaybackQueueIndex = 1
        };
        _settingsService.GetRestorePlaybackStateEnabledAsync().Returns(true);
        _settingsService.GetPlaybackStateAsync().Returns(savedState);

        _library.Reader.GetSongByIdAsync(_testSongs[1].Id).Returns((Song?)null);

        await _service.InitializeAsync();

        _service.PlaybackQueue.Should().BeEquivalentTo(songIds);
        _service.CurrentTrack.Should().BeNull();
        _service.CurrentQueueIndex.Should().Be(0);
        await _audioPlayer.DidNotReceive().LoadAsync(Arg.Any<Song>());
    }

    [Fact]
    public async Task PlayAsync_WithSongList_ReplacesQueueAndPlaysFromStartIndex()
    {
        await _service.InitializeAsync();

        await _service.PlayAsync(_testSongs, 2);

        _service.PlaybackQueue.Should().BeEquivalentTo(_testSongs.Select(s => s.Id), options => options.WithStrictOrdering());
        _service.CurrentTrack.Should().Be(_testSongs[2]);
        _service.CurrentQueueIndex.Should().Be(2);
        await _audioPlayer.Received(1).LoadAsync(_testSongs[2]);
        await _audioPlayer.Received(1).PlayAsync();
    }

    [Fact]
    public async Task PlayAsync_WithEmptyList_StopsAndClearsQueue()
    {
        await _service.InitializeAsync();
        await _service.PlayAsync(_testSongs);

        await _service.PlayAsync(new List<Song>());

        await _audioPlayer.Received(1).StopAsync();
        _service.PlaybackQueue.Should().BeEmpty();
        _service.CurrentTrack.Should().BeNull();
    }

    [Fact]
    public async Task PlayAsync_WithInvalidStartIndex_PlaysFromBeginning()
    {
        await _service.InitializeAsync();

        await _service.PlayAsync(_testSongs, 99);

        _service.CurrentTrack.Should().Be(_testSongs[0]);
        _service.CurrentQueueIndex.Should().Be(0);
        await _audioPlayer.Received(1).LoadAsync(_testSongs[0]);
    }

    [Fact]
    public async Task PlayPauseAsync_WhenPlaying_PausesPlayback()
    {
        await _service.InitializeAsync();
        await _service.PlayAsync(_testSongs[0]);
        _audioPlayer.IsPlaying.Returns(true);

        await _service.PlayPauseAsync();

        await _audioPlayer.Received(1).PauseAsync();
    }

    [Fact]
    public async Task PlayPauseAsync_WhenStoppedWithQueue_PlaysFromCurrentIndex()
    {
        await _service.InitializeAsync();

        await _service.PlayAsync(_testSongs, 2);
        await _service.StopAsync();
        _audioPlayer.IsPlaying.Returns(false);
        _audioPlayer.ClearReceivedCalls();

        await _service.PlayPauseAsync();

        _service.CurrentTrack.Should().Be(_testSongs[2]);
        await _audioPlayer.Received(1).LoadAsync(_testSongs[2]);
    }

    [Fact]
    public async Task NextAsync_WithRepeatOne_RestartsCurrentTrack()
    {
        await _service.InitializeAsync();
        await _service.SetRepeatModeAsync(RepeatMode.RepeatOne);
        await _service.PlayAsync(_testSongs, 1);
        _audioPlayer.ClearReceivedCalls();

        await _service.NextAsync();

        _service.CurrentTrack.Should().Be(_testSongs[1]);
        await _audioPlayer.Received(1).LoadAsync(_testSongs[1]);
        await _audioPlayer.Received(1).PlayAsync();
    }

    [Fact]
    public async Task NextAsync_AtEndOfQueueWithRepeatAll_PlaysFirstSong()
    {
        await _service.InitializeAsync();
        await _service.SetRepeatModeAsync(RepeatMode.RepeatAll);
        await _service.PlayAsync(_testSongs, _testSongs.Count - 1);

        await _service.NextAsync();

        _service.CurrentTrack.Should().Be(_testSongs[0]);
        await _audioPlayer.Received(1).LoadAsync(_testSongs[0]);
    }

    [Fact]
    public async Task NextAsync_AtEndOfQueueWithNoRepeat_StopsPlayback()
    {
        await _service.InitializeAsync();
        await _service.PlayAsync(_testSongs, _testSongs.Count - 1);

        await _service.NextAsync();

        _service.CurrentTrack.Should().BeNull();
        await _audioPlayer.Received(1).StopAsync();
    }

    [Fact]
    public async Task PreviousAsync_WithRepeatOne_RestartsCurrentTrack()
    {
        await _service.InitializeAsync();
        await _service.SetRepeatModeAsync(RepeatMode.RepeatOne);
        await _service.PlayAsync(_testSongs, 1);
        _audioPlayer.CurrentPosition.Returns(TimeSpan.FromSeconds(10));
        _audioPlayer.ClearReceivedCalls();

        await _service.PreviousAsync();

        _service.CurrentTrack.Should().Be(_testSongs[1]);
        await _audioPlayer.Received(1).LoadAsync(_testSongs[1]);
        await _audioPlayer.Received(1).PlayAsync();
    }

    [Fact]
    public async Task PreviousAsync_AtStartOfQueueWithRepeatAll_PlaysLastSong()
    {
        await _service.InitializeAsync();
        await _service.SetRepeatModeAsync(RepeatMode.RepeatAll);
        await _service.PlayAsync(_testSongs);
        _audioPlayer.CurrentPosition.Returns(TimeSpan.FromSeconds(1));

        await _service.PreviousAsync();

        _service.CurrentTrack.Should().Be(_testSongs.Last());
        await _audioPlayer.Received(1).LoadAsync(_testSongs.Last());
    }

    [Fact]
    public async Task PlayAsync_CreatesListenSessionAndSetsContext()
    {
        await _service.InitializeAsync();
        _library.Writer.StartListenSessionAsync(Arg.Any<Guid>(), Arg.Any<PlaybackContext>()).Returns(100L);

        await _service.PlayAsync(_testSongs[0]);

        _service.CurrentListenHistoryId.Should().Be(100L);
        await _library.Writer.Received(1).StartListenSessionAsync(
            _testSongs[0].Id,
            Arg.Is<PlaybackContext>(c => c != null && c.Type == PlaybackContextType.Library && c.ContextId == null));
    }

    [Fact]
    public async Task PlayAsync_RaisesTrackChanged_AfterListenSessionIdIsSet()
    {
        await _service.InitializeAsync();
        _library.Writer.StartListenSessionAsync(Arg.Any<Guid>(), Arg.Any<PlaybackContext>()).Returns(150L);

        long? listenHistoryIdSeenByEvent = null;
        _service.TrackChanged += () => listenHistoryIdSeenByEvent = _service.CurrentListenHistoryId;

        _audioPlayer.When(x => x.LoadAsync(Arg.Any<Song>())).Do(_ =>
        {
            _audioPlayer.MediaOpened += Raise.Event<Action>();
        });

        await _service.PlayAsync(_testSongs[0]);

        listenHistoryIdSeenByEvent.Should().Be(150L,
            "TrackChanged consumers (e.g. presence) must always see the active listen session ID");
    }

    [Fact]
    public async Task StopAsync_WithActiveSession_FinalizesSessionAsPausedAndAbandoned()
    {
        await _service.InitializeAsync();
        _library.Writer.StartListenSessionAsync(Arg.Any<Guid>(), Arg.Any<PlaybackContext>()).Returns(200L);
        await _service.PlayAsync(_testSongs[0]);
        _audioPlayer.CurrentPosition.Returns(TimeSpan.FromSeconds(30));

        await _service.StopAsync();
        await _service.FlushPendingFinalizationAsync();

        _service.CurrentListenHistoryId.Should().BeNull();
        await _library.Writer.Received(1).FinalizeListenSessionAsync(
            200L,
            TimeSpan.FromSeconds(30),
            PlaybackEndReason.PausedAndAbandoned);
    }

    [Fact]
    public async Task NextAsync_WithActiveSession_FinalizesSessionAsSkipped()
    {
        await _service.InitializeAsync();
        _library.Writer.StartListenSessionAsync(Arg.Any<Guid>(), Arg.Any<PlaybackContext>()).Returns(300L);
        await _service.PlayAsync(_testSongs, 0);
        _audioPlayer.CurrentPosition.Returns(TimeSpan.FromSeconds(45));

        _library.Writer.StartListenSessionAsync(Arg.Any<Guid>(), Arg.Any<PlaybackContext>()).Returns(301L);

        await _service.NextAsync();
        await _service.FlushPendingFinalizationAsync();

        await _library.Writer.Received(1).FinalizeListenSessionAsync(
            300L,
            TimeSpan.FromSeconds(45),
            PlaybackEndReason.Skipped);

        _service.CurrentListenHistoryId.Should().Be(301L);
        _service.CurrentTrack.Should().Be(_testSongs[1]);
        await _audioPlayer.Received(1).LoadAsync(_testSongs[1]);
    }

    [Fact]
    public async Task OnPlaybackEnded_FinalizesSessionAsFinishedAndAdvancesQueue()
    {
        await _service.InitializeAsync();
        _library.Writer.StartListenSessionAsync(Arg.Any<Guid>(), Arg.Any<PlaybackContext>()).Returns(400L);
        await _service.PlayAsync(_testSongs, 0);

        var duration = TimeSpan.FromMinutes(3);
        _audioPlayer.Duration.Returns(duration);
        _audioPlayer.CurrentPosition.Returns(duration);

        _audioPlayer.PlaybackEnded += Raise.Event<Action>();
        await _service.FlushPendingFinalizationAsync();

        await _library.Writer.Received(1).FinalizeListenSessionAsync(
            400L,
            duration,
            PlaybackEndReason.Finished);

        _service.CurrentTrack.Should().Be(_testSongs[1]);
        await _audioPlayer.Received(1).LoadAsync(_testSongs[1]);
    }

    [Theory]
    [InlineData(PlaybackContextType.Album)]
    [InlineData(PlaybackContextType.Artist)]
    [InlineData(PlaybackContextType.Folder)]
    [InlineData(PlaybackContextType.Genre)]
    [InlineData(PlaybackContextType.Playlist)]
    public async Task PlayingACollection_StartsTheListenSessionWithThatCollectionAsContext(PlaybackContextType type)
    {
        await _service.InitializeAsync();
        var id = Guid.NewGuid();
        var songIds = _testSongs.Select(s => s.Id).ToList();
        _library.Writer.StartListenSessionAsync(Arg.Any<Guid>(), Arg.Any<PlaybackContext>()).Returns(500L);

        switch (type)
        {
            case PlaybackContextType.Album:
                _library.Reader.GetAllSongIdsByAlbumIdAsync(id, Arg.Any<SongSortOrder>()).Returns(songIds);
                await _service.PlayAlbumAsync(id);
                break;
            case PlaybackContextType.Artist:
                _library.Reader.GetAllSongIdsByArtistIdAsync(id, Arg.Any<SongSortOrder>()).Returns(songIds);
                await _service.PlayArtistAsync(id);
                break;
            case PlaybackContextType.Folder:
                _library.Reader.GetAllSongIdsByFolderIdAsync(id, Arg.Any<SongSortOrder>()).Returns(songIds);
                await _service.PlayFolderAsync(id);
                break;
            case PlaybackContextType.Genre:
                _library.Reader.GetAllSongIdsByGenreIdAsync(id, Arg.Any<SongSortOrder>()).Returns(songIds);
                await _service.PlayGenreAsync(id);
                break;
            default:
                _library.Reader.GetAllSongIdsByPlaylistIdAsync(id, Arg.Any<SongSortOrder>()).Returns(songIds);
                await _service.PlayPlaylistAsync(id);
                break;
        }

        await _library.Writer.Received(1).StartListenSessionAsync(
            _testSongs[0].Id, Arg.Is<PlaybackContext>(c => c != null && c.Type == type && c.ContextId == id));
        _service.CurrentListenHistoryId.Should().Be(500L);
    }

    [Fact]
    public async Task DisposeAsync_FinalizesActiveSessionAsPausedAndAbandoned()
    {
        await _service.InitializeAsync();
        _library.Writer.StartListenSessionAsync(Arg.Any<Guid>(), Arg.Any<PlaybackContext>()).Returns(600L);
        await _service.PlayAsync(_testSongs[0]);
        _audioPlayer.CurrentPosition.Returns(TimeSpan.FromSeconds(15));

        await _service.DisposeAsync();

        await _library.Writer.Received(1).FinalizeListenSessionAsync(
            600L,
            TimeSpan.FromSeconds(15),
            PlaybackEndReason.PausedAndAbandoned);
    }

    [Fact]
    public async Task PlayTransientFileAsync_StartsTransientSessionAndFinalizesPrevious()
    {
        await _service.InitializeAsync();
        _library.Writer.StartListenSessionAsync(Arg.Any<Guid>(), Arg.Any<PlaybackContext>()).Returns(100L);

        await _service.PlayAsync(_testSongs[0]);

        var filePath = "C:\\temp\\transient.mp3";
        var metadata = new SongFileMetadata { Title = "Transient Song", Artists = new List<string> { "Temp Artist" } };
        _metadataService.ExtractMetadataAsync(filePath).Returns(metadata);

        _audioPlayer.CurrentPosition.Returns(TimeSpan.FromSeconds(20));
        _audioPlayer.ClearReceivedCalls();
        _library.ClearReceivedCalls();

        await _service.PlayTransientFileAsync(filePath);
        await _service.FlushPendingFinalizationAsync();

        await _library.Writer.Received(1).FinalizeListenSessionAsync(
            100L,
            TimeSpan.FromSeconds(20),
            PlaybackEndReason.Skipped);

        _service.CurrentListenHistoryId.Should().BeNull();
        await _library.Writer.DidNotReceiveWithAnyArgs().StartListenSessionAsync(Guid.Empty, default!);
    }

    [Fact]
    public async Task PlayTransientFileAsync_WhenFileExistsInLibrary_StartsTransientSession()
    {
        await _service.InitializeAsync();

        const string filePath = "C:\\temp\\transient-in-library.mp3";
        var metadata = new SongFileMetadata
        {
            Title = "Transient Song",
            Artists = new List<string> { "Temp Artist" }
        };
        _metadataService.ExtractMetadataAsync(filePath).Returns(metadata);

        var mappedSong = new Song
        {
            Id = Guid.NewGuid(),
            Title = "Library Version",
            FilePath = filePath,
            Folder = new Folder { Path = "C:\\temp" }
        };

        _library.Reader.GetSongByFilePathAsync(filePath).Returns(mappedSong);
        _library.Writer.StartListenSessionAsync(mappedSong.Id, Arg.Any<PlaybackContext>()).Returns(777L);

        await _service.PlayTransientFileAsync(filePath);

        _service.CurrentListenHistoryId.Should().Be(777L);
        await _library.Writer.Received(1).StartListenSessionAsync(
            mappedSong.Id,
            Arg.Is<PlaybackContext>(c => c != null && c.Type == PlaybackContextType.Transient && c.ContextId == null));
    }

    [Fact]
    public async Task PlayQueueItemAsync_WhenSkippingDeletedSongs_FinalizesPreviousSessionCorrectly()
    {

        await _service.InitializeAsync();
        _library.Writer.StartListenSessionAsync(Arg.Any<Guid>(), Arg.Any<PlaybackContext>()).Returns(700L);
        await _service.PlayAsync(_testSongs[0]);

        _audioPlayer.CurrentPosition.Returns(TimeSpan.FromSeconds(15));

        await _service.AddToQueueAsync(_testSongs[1]);
        await _service.AddToQueueAsync(_testSongs[2]);

        _library.Reader.GetSongByIdAsync(_testSongs[1].Id).Returns((Song?)null);

        _library.Reader.GetSongByIdAsync(_testSongs[2].Id).Returns(_testSongs[2]);
        _library.Writer.StartListenSessionAsync(Arg.Any<Guid>(), Arg.Any<PlaybackContext>()).Returns(701L);

        await _service.PlayQueueItemAsync(1);
        await _service.FlushPendingFinalizationAsync();

        await _library.Writer.Received(1).FinalizeListenSessionAsync(
            700L,
            TimeSpan.FromSeconds(15),
            PlaybackEndReason.Skipped);

        _service.CurrentTrack.Should().Be(_testSongs[2]);
        _service.CurrentListenHistoryId.Should().Be(701L);
    }

    [Fact]
    public async Task PreviousAsync_Under3Seconds_FinalizesSessionAsSkipped()
    {
        await _service.InitializeAsync();
        _library.Writer.StartListenSessionAsync(Arg.Any<Guid>(), Arg.Any<PlaybackContext>()).Returns(900L);
        await _service.PlayAsync(_testSongs, 1);

        _audioPlayer.CurrentPosition.Returns(TimeSpan.FromSeconds(2));

        _library.Writer.StartListenSessionAsync(Arg.Any<Guid>(), Arg.Any<PlaybackContext>()).Returns(901L);

        await _service.PreviousAsync();
        await _service.FlushPendingFinalizationAsync();

        await _library.Writer.Received(1).FinalizeListenSessionAsync(
            900L,
            TimeSpan.FromSeconds(2),
            PlaybackEndReason.Skipped);

        _service.CurrentListenHistoryId.Should().Be(901L);
        await _audioPlayer.Received(1).LoadAsync(_testSongs[0]);
        _service.CurrentTrack.Should().Be(_testSongs[0]);
    }

    [Fact]
    public async Task PreviousAsync_Over3Seconds_DoesNotFinalizeSession()
    {
        await _service.InitializeAsync();
        _library.Writer.StartListenSessionAsync(Arg.Any<Guid>(), Arg.Any<PlaybackContext>()).Returns(1000L);
        await _service.PlayAsync(_testSongs, 1);

        _audioPlayer.CurrentPosition.Returns(TimeSpan.FromSeconds(10));
        _library.ClearReceivedCalls();

        await _service.PreviousAsync();
        await _service.FlushPendingFinalizationAsync();

        await _library.Writer.DidNotReceiveWithAnyArgs().FinalizeListenSessionAsync(default, default, default);

        _service.CurrentListenHistoryId.Should().Be(1000L);
        _service.CurrentTrack.Should().Be(_testSongs[1]);

        await _audioPlayer.Received(1).SeekAsync(TimeSpan.Zero);
    }

    [Fact]
    public async Task PlayAsync_WithExplicitContext_StartsSessionWithThatContext()
    {

        await _service.InitializeAsync();
        var smartPlaylistId = Guid.NewGuid();
        var context = new PlaybackContext(PlaybackContextType.SmartPlaylist, smartPlaylistId);
        _library.Writer.StartListenSessionAsync(Arg.Any<Guid>(), Arg.Any<PlaybackContext>()).Returns(1500L);

        await _service.PlayAsync(_testSongs.Select(s => s.Id).ToList(), 0, null, context);

        await _library.Writer.Received(1).StartListenSessionAsync(
            _testSongs[0].Id,
            Arg.Is<PlaybackContext>(c => c != null && c.Type == PlaybackContextType.SmartPlaylist && c.ContextId == smartPlaylistId));
        _service.CurrentListenHistoryId.Should().Be(1500L);
    }

    [Fact]
    public async Task SetShuffleAsync_WhenEnabled_GeneratesShuffledQueue()
    {
        await _service.InitializeAsync();
        await _service.PlayAsync(_testSongs);
        var eventTracker = new EventTracker(_service);

        await _service.SetShuffleAsync(true);

        _service.IsShuffleEnabled.Should().BeTrue();
        _service.CurrentTrack.Should().Be(_testSongs[0]);
        _service.ShuffledQueue.Should().BeEquivalentTo(_testSongs.Select(s => s.Id));
        await _settingsService.Received(1).SaveShuffleStateAsync(true);
        eventTracker.ShuffleModeChangedCount.Should().Be(1);

        eventTracker.QueueChangedCount.Should().Be(1);
    }

    [Fact]
    public async Task SetShuffleAsync_WhenDisabled_ClearsShuffledQueue()
    {
        await _service.InitializeAsync();
        await _service.PlayAsync(_testSongs);
        await _service.SetShuffleAsync(true);
        _service.ShuffledQueue.Should().NotBeEmpty();

        await _service.SetShuffleAsync(false);

        _service.IsShuffleEnabled.Should().BeFalse();
        _service.ShuffledQueue.Should().BeEmpty();
        await _settingsService.Received(1).SaveShuffleStateAsync(false);
    }

    [Fact]
    public async Task PlayAsync_WithShuffleEnabledAndStartIndex_PlaysSelectedSongFirst()
    {
        await _service.InitializeAsync();
        await _service.SetShuffleAsync(true);
        var targetIndex = 2;
        var targetSong = _testSongs[targetIndex];

        await _service.PlayAsync(_testSongs, targetIndex, startShuffled: null);

        _service.IsShuffleEnabled.Should().BeTrue();
        _service.CurrentTrack.Should().Be(targetSong);

        _service.ShuffledQueue.First().Should().Be(targetSong.Id);

        await _audioPlayer.Received(1).LoadAsync(targetSong);
    }

    [Fact]
    public async Task AddToQueueAsync_WithShuffleEnabled_AddsToBothQueues()
    {
        await _service.InitializeAsync();
        await _service.PlayAsync(_testSongs);
        await _service.SetShuffleAsync(true);
        var songToAdd = new Song { Id = Guid.NewGuid(), Title = "New Song" };

        await _service.AddToQueueAsync(songToAdd);

        _service.PlaybackQueue.Should().Contain(songToAdd.Id);
        _service.ShuffledQueue.Should().Contain(songToAdd.Id);
        _service.PlaybackQueue.Count.Should().Be(_testSongs.Count + 1);
        _service.ShuffledQueue.Count.Should().Be(_testSongs.Count + 1);
    }

    [Fact]
    public async Task PlayNextAsync_WhenCalled_InsertsSongAfterCurrentTrack()
    {
        await _service.InitializeAsync();
        await _service.PlayAsync(_testSongs, 1);
        var songToPlayNext = new Song { Id = Guid.NewGuid(), Title = "Next Song" };
        _library.Reader.GetSongByIdAsync(songToPlayNext.Id).Returns(songToPlayNext);

        await _service.PlayNextAsync(songToPlayNext);

        _service.PlaybackQueue.Should().HaveCount(_testSongs.Count + 1);
        _service.PlaybackQueue[2].Should().Be(songToPlayNext.Id);
    }

    [Fact]
    public async Task PlayNextAsync_WithSongAlreadyInQueue_MovesSongToNextPosition()
    {
        await _service.InitializeAsync();
        await _service.PlayAsync(_testSongs);
        var songToMove = _testSongs[3];

        await _service.PlayNextAsync(songToMove);

        _service.PlaybackQueue.Should().HaveCount(_testSongs.Count);
        _service.PlaybackQueue[1].Should().Be(songToMove.Id);
    }

    [Fact]
    public async Task PlayNextAsync_WithEmptyQueue_AddsSongAtBeginning()
    {
        await _service.InitializeAsync();
        var songToAdd = _testSongs[0];

        await _service.PlayNextAsync(songToAdd);

        _service.PlaybackQueue.Should().HaveCount(1);
        _service.PlaybackQueue[0].Should().Be(songToAdd.Id);
    }

    [Fact]
    public async Task RemoveRangeFromQueueAsync_WhenRemovingCurrentTrack_CapturesPositionBeforePlayerStops()
    {
        await _service.InitializeAsync();
        _library.Writer.StartListenSessionAsync(Arg.Any<Guid>(), Arg.Any<PlaybackContext>()).Returns(2101L);
        await _service.PlayAsync(_testSongs, 1);

        var position = TimeSpan.FromSeconds(91);
        _audioPlayer.CurrentPosition.Returns(_ => position);
        _audioPlayer.StopAsync().Returns(_ =>
        {
            position = TimeSpan.Zero;
            return Task.CompletedTask;
        });
        _library.Writer.StartListenSessionAsync(Arg.Any<Guid>(), Arg.Any<PlaybackContext>()).Returns(2102L);

        await _service.RemoveRangeFromQueueAsync(new[] { _testSongs[1].Id });
        await _service.FlushPendingFinalizationAsync();

        await _library.Writer.Received(1).FinalizeListenSessionAsync(
            2101L,
            TimeSpan.FromSeconds(91),
            PlaybackEndReason.Skipped);
        _service.CurrentListenHistoryId.Should().Be(2102L);
    }

    [Fact]
    public async Task RemoveRangeFromQueueAsync_WhenCurrentAndFollowingTrackAreRemoved_PlaysNextSurvivor()
    {
        await _service.InitializeAsync();
        await _service.PlayAsync(_testSongs, 1);

        await _service.RemoveRangeFromQueueAsync(new[] { _testSongs[1].Id, _testSongs[2].Id });

        _service.CurrentTrack.Should().Be(_testSongs[3]);
        await _audioPlayer.Received(1).StopAsync();
        await _audioPlayer.Received(1).LoadAsync(_testSongs[3]);
    }

    [Fact]
    public async Task RemoveRangeFromQueueAsync_WhenCurrentTrackIsRemoved_KeepsTheSleepTimer()
    {
        await _service.InitializeAsync();
        await _service.PlayAsync(_testSongs, 1);
        _service.ScheduleStop(PlaybackStopMode.Timer, TimeSpan.FromMinutes(30));

        await _service.RemoveRangeFromQueueAsync(new[] { _testSongs[1].Id });

        _service.CurrentTrack.Should().Be(_testSongs[2]);
        _service.StopMode.Should().Be(PlaybackStopMode.Timer);
    }

    [Fact]
    public async Task RemoveRangeFromQueueAsync_WhenLastCurrentTrackIsRemovedWithoutRepeat_Stops()
    {
        await _service.InitializeAsync();
        await _service.PlayAsync(_testSongs, _testSongs.Count - 1);

        await _service.RemoveRangeFromQueueAsync(new[] { _testSongs[^1].Id });

        _service.CurrentTrack.Should().BeNull();
        _service.PlaybackQueue.Should().NotBeEmpty();
        await _audioPlayer.Received(1).StopAsync();
    }

    [Fact]
    public async Task RemoveRangeFromQueueAsync_WhenLastCurrentTrackIsRemovedWithRepeatAll_WrapsToFirstSurvivor()
    {
        await _service.InitializeAsync();
        await _service.SetRepeatModeAsync(RepeatMode.RepeatAll);
        await _service.PlayAsync(_testSongs, _testSongs.Count - 1);

        await _service.RemoveRangeFromQueueAsync(new[] { _testSongs[^1].Id });

        _service.CurrentTrack.Should().Be(_testSongs[0]);
    }

    [Fact]
    public async Task RemoveFromQueueAsync_WhenRemovingTrackBeforeCurrent_AdjustsCurrentIndex()
    {
        await _service.InitializeAsync();

        await _service.PlayAsync(_testSongs, 2);
        var songToRemove = _testSongs[0];

        await _service.RemoveFromQueueAsync(songToRemove);

        _service.PlaybackQueue.Should().NotContain(songToRemove.Id);
        _service.CurrentTrack.Should().Be(_testSongs[2]);
        _service.CurrentQueueIndex.Should().Be(1);
        await _audioPlayer.DidNotReceive().StopAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClearQueueAsync_WithLargeQueue_ClearsPlaybackAndSavedState(bool shuffled)
    {
        await _service.InitializeAsync();
        var ids = new[] { _testSongs[0].Id }.Concat(Enumerable.Range(0, 50000).Select(_ => Guid.NewGuid())).ToList();
        await _service.PlayAsync(ids);
        await _service.SetShuffleAsync(shuffled);
        PlaybackState? savedState = null;
        _settingsService.SavePlaybackStateAsync(Arg.Do<PlaybackState>(state => savedState = state))
            .Returns(Task.CompletedTask);

        await _service.ClearQueueAsync();
        await _service.SavePlaybackStateAsync();

        _service.PlaybackQueue.Should().BeEmpty();
        _service.ShuffledQueue.Should().BeEmpty();
        _service.CurrentTrack.Should().BeNull();
        _service.CurrentQueueIndex.Should().Be(-1);
        _service.CurrentShuffledIndex.Should().Be(-1);
        await _audioPlayer.Received(1).StopAsync();
        savedState.Should().NotBeNull();
        savedState!.PlaybackQueueTrackIds.Should().BeEmpty();
        savedState.ShuffledQueueTrackIds.Should().BeEmpty();
        savedState.CurrentTrackId.Should().BeNull();

        _settingsService.GetRestorePlaybackStateEnabledAsync().Returns(true);
        _settingsService.GetPlaybackStateAsync().Returns(savedState);
        using var restored = new MusicPlaybackService(_settingsService, _audioPlayer, _mediaControls, _library.Reader, _library.Writer, _metadataService, _fileSystem, _logger);
        await restored.InitializeAsync();
        restored.PlaybackQueue.Should().BeEmpty();
        restored.CurrentTrack.Should().BeNull();
    }

    [Fact]
    public async Task ClearQueueAsync_WithExternalFile_DiscardsMetadataBeforeAddingItAgain()
    {
        const string path = "C:\\temp\\queue.mp3";
        _metadataService.ExtractMetadataAsync(path).Returns(new SongFileMetadata { Title = "Before edit" });
        _library.Reader.GetSongsByIdsAsync(Arg.Any<IEnumerable<Guid>>())
            .Returns(new Dictionary<Guid, Song>());
        await _service.InitializeAsync();
        await _service.AddTransientFilesToQueueAsync([path]);
        var previousId = _service.CurrentTrack!.Id;

        await _service.ClearQueueAsync();
        (await _service.GetQueueTracksAsync([previousId])).Should().BeEmpty();
        _metadataService.ExtractMetadataAsync(path).Returns(new SongFileMetadata { Title = "After edit" });
        await _service.AddTransientFilesToQueueAsync([path]);

        _service.PlaybackQueue.Should().ContainSingle();
        _service.CurrentTrack!.Title.Should().Be("After edit");
        _service.CurrentTrack.Id.Should().NotBe(previousId);
        await _metadataService.Received(2).ExtractMetadataAsync(path);
    }

    [Fact]
    public async Task SetRepeatModeAsync_WhenChanged_UpdatesPropertyAndSaves()
    {
        await _service.InitializeAsync();
        var eventTracker = new EventTracker(_service);

        await _service.SetRepeatModeAsync(RepeatMode.RepeatOne);

        _service.CurrentRepeatMode.Should().Be(RepeatMode.RepeatOne);
        await _settingsService.Received(1).SaveRepeatModeAsync(RepeatMode.RepeatOne);
        eventTracker.RepeatModeChangedCount.Should().Be(1);
    }

    [Fact]
    public async Task SavePlaybackStateAsync_WhenCalled_SavesCorrectState()
    {
        await _service.InitializeAsync();

        await _service.PlayAsync(_testSongs, 2);
        _audioPlayer.CurrentPosition.Returns(TimeSpan.FromSeconds(45));

        await _service.SavePlaybackStateAsync();

        await _settingsService.Received(1).SavePlaybackStateAsync(Arg.Is<PlaybackState>(state =>
            state != null &&
            state.CurrentTrackId == _testSongs[2].Id &&
            state.PlaybackQueueTrackIds.SequenceEqual(_testSongs.Select(s => s.Id)) &&
            state.CurrentPlaybackQueueIndex == 2
        ));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SettingABandOrThePreamp_ChangesOnlyThatValueAndSavesIt(bool preamp)
    {
        await _service.InitializeAsync();
        var bandsBefore = _service.CurrentEqualizerSettings!.BandGains.ToList();
        var preampBefore = _service.CurrentEqualizerSettings.Preamp;
        _audioPlayer.ClearReceivedCalls();
        _settingsService.ClearReceivedCalls();
        var eventTracker = new EventTracker(_service);

        if (preamp) await _service.SetEqualizerPreampAsync(5.0f);
        else await _service.SetEqualizerBandAsync(1, 3.5f);

        var after = _service.CurrentEqualizerSettings!;
        after.Preamp.Should().Be(preamp ? 5.0f : preampBefore);
        after.BandGains.Should().Equal(preamp ? bandsBefore : bandsBefore.Select((gain, i) => i == 1 ? 3.5f : gain));
        _audioPlayer.Received(1).ApplyEqualizerSettings(after);
        await _settingsService.Received(1).SetEqualizerSettingsAsync(after);
        eventTracker.EqualizerChangedCount.Should().Be(1);
    }

    [Fact]
    public async Task ResetEqualizerAsync_WhenCalled_ResetsAllGainsToDefault()
    {
        await _service.InitializeAsync();
        await _service.SetEqualizerBandAsync(0, 5.0f);
        await _service.SetEqualizerPreampAsync(2.0f);
        _audioPlayer.ClearReceivedCalls();
        _settingsService.ClearReceivedCalls();

        await _service.ResetEqualizerAsync();

        _service.CurrentEqualizerSettings!.Preamp.Should().Be(EqualizerSettings.DefaultPreampDb);
        _service.CurrentEqualizerSettings.BandGains.Should().AllSatisfy(g => g.Should().Be(0.0f));
        _audioPlayer.Received(1)
            .ApplyEqualizerSettings(
                Arg.Is<EqualizerSettings>(s => s != null &&
                                                     s.Preamp == EqualizerSettings.DefaultPreampDb &&
                                                     s.BandGains.All(g => g == 0.0f)));
        await _settingsService.Received(1).SetEqualizerSettingsAsync(Arg.Any<EqualizerSettings>());
    }

    [Fact]
    public async Task OnAudioPlayerErrorOccurred_ShouldStopPlayback()
    {
        await _service.InitializeAsync();
        await _service.PlayAsync(_testSongs[0]);

        _audioPlayer.ErrorOccurred += Raise.Event<Action<string>>("File not found");

        await _service.FlushPendingFinalizationAsync();
        _service.CurrentTrack.Should().BeNull();
        await _audioPlayer.Received(1).StopAsync();
    }

    [Fact]
    public async Task MediaControlsNextPressed_ShouldCallNextAsync()
    {
        await _service.InitializeAsync();
        await _service.PlayAsync(_testSongs);

        _mediaControls.NextPressed += Raise.Event<Action>();

        await _service.FlushPendingFinalizationAsync();
        _service.CurrentTrack.Should().Be(_testSongs[1]);
    }

    [Fact]
    public async Task MediaControlsPreviousPressed_ShouldCallPreviousAsync()
    {
        await _service.InitializeAsync();
        await _service.PlayAsync(_testSongs, 1);
        _audioPlayer.CurrentPosition.Returns(TimeSpan.FromSeconds(1));

        _mediaControls.PreviousPressed += Raise.Event<Action>();

        await _service.FlushPendingFinalizationAsync();
        _service.CurrentTrack.Should().Be(_testSongs[0]);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(4, false)]
    public async Task MediaControlsNavigation_FollowsThePositionInTheQueue(int startIndex, bool canGoNext)
    {
        await _service.InitializeAsync();
        _mediaControls.ClearReceivedCalls();

        await _service.PlayAsync(_testSongs, startIndex);
        _audioPlayer.MediaOpened += Raise.Event<Action>();

        _mediaControls.ReceivedCalls().Last(call => call.GetMethodInfo().Name == nameof(ISystemMediaControls.SetNavigationEnabled))
            .GetArguments().Should().Equal(canGoNext, true);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PlayNextAsync_PreservesRequestedOrderAfterMovingEarlierTracks(bool shuffled)
    {
        await _service.InitializeAsync();
        await _service.PlayAsync(_testSongs, 2);
        await _service.SetShuffleAsync(shuffled);

        await _service.PlayNextAsync(_testSongs[0]);
        await _service.PlayNextAsync(_testSongs[1]);

        _service.PlaybackQueue.Take(3).Should().Equal(_testSongs[2].Id, _testSongs[1].Id, _testSongs[0].Id);
        await _service.NextAsync();
        _service.CurrentTrack.Should().Be(_testSongs[1]);
        await _service.NextAsync();
        _service.CurrentTrack.Should().Be(_testSongs[0]);
    }

    [Fact]
    public async Task ClearQueueAsync_WithStandaloneExternalFile_StopsPlayback()
    {
        const string path = "C:\\temp\\standalone.mp3";
        _metadataService.ExtractMetadataAsync(path).Returns(new SongFileMetadata { Title = "External" });
        await _service.PlayTransientFileAsync(path);

        await _service.ClearQueueAsync();

        _service.CurrentTrack.Should().BeNull();
        await _audioPlayer.Received(1).StopAsync();
    }

    [Theory]
    [InlineData("lookup", false)]
    [InlineData("lookup", true)]
    [InlineData("session", false)]
    [InlineData("session", true)]
    [InlineData("load", false)]
    [InlineData("load", true)]
    public async Task PlayAsync_WhenSuperseded_DoesNotResumeOrReplaceNewerState(string delayAt, bool stop)
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = _testSongs[0];
        var second = _testSongs[1];
        _library.Writer.StartListenSessionAsync(first.Id, Arg.Any<PlaybackContext>()).Returns(101L);
        _library.Writer.StartListenSessionAsync(second.Id, Arg.Any<PlaybackContext>()).Returns(202L);
        if (delayAt == "lookup")
            _library.Reader.GetSongByIdAsync(first.Id).Returns(async _ => { await release.Task; return (Song?)first; });
        else if (delayAt == "session")
            _library.Writer.StartListenSessionAsync(first.Id, Arg.Any<PlaybackContext>())
                .Returns(async _ => { await release.Task; return (long?)101L; });
        else
            _audioPlayer.LoadAsync(first).Returns(release.Task);

        var oldRequest = _service.PlayAsync(first);
        if (stop) await _service.StopAsync();
        else await _service.PlayAsync(second);
        release.SetResult();
        await oldRequest.WaitAsync(TimeSpan.FromSeconds(2));
        await _service.FlushPendingFinalizationAsync();

        _service.CurrentTrack.Should().Be(stop ? null : second);
        _service.CurrentListenHistoryId.Should().Be(stop ? null : 202L);
        await _audioPlayer.Received(stop ? 0 : 1).PlayAsync();
        if (delayAt == "session")
            await _library.Writer.Received(1).FinalizeListenSessionAsync(101L, TimeSpan.Zero, PlaybackEndReason.PausedAndAbandoned);
    }

    [Fact]
    public async Task PlayAlbumAsync_WhenLookupFinishesAfterNewSelection_KeepsNewQueue()
    {
        var release = new TaskCompletionSource<List<Guid>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var albumId = Guid.NewGuid();
        _library.Reader.GetAllSongIdsByAlbumIdAsync(albumId, SongSortOrder.TrackNumberAsc).Returns(release.Task);
        var oldRequest = _service.PlayAlbumAsync(albumId);

        await _service.PlayAsync(_testSongs[1]);
        release.SetResult([_testSongs[0].Id]);
        await oldRequest;

        _service.CurrentTrack.Should().Be(_testSongs[1]);
        _service.PlaybackQueue.Should().Equal(_testSongs[1].Id);
    }

    [Fact]
    public async Task PlaybackEnded_WhileAnotherTrackIsLoading_DoesNotSkipTheRequestedTrack()
    {
        await _service.PlayAsync(_testSongs);
        var release = new TaskCompletionSource<Song?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _library.Reader.GetSongByIdAsync(_testSongs[1].Id).Returns(release.Task);
        var next = _service.NextAsync();

        _audioPlayer.PlaybackEnded += Raise.Event<Action>();
        release.SetResult(_testSongs[1]);
        await next;
        await _service.FlushPendingFinalizationAsync();

        _service.CurrentTrack.Should().Be(_testSongs[1]);
        await _audioPlayer.Received(1).LoadAsync(_testSongs[1]);
        await _audioPlayer.DidNotReceive().LoadAsync(_testSongs[2]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClearQueueAsync_WhenStopFinishesAfterNewPlayback_PreservesNewSelection(bool removeCurrent)
    {
        await _service.PlayAsync(_testSongs);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _audioPlayer.StopAsync().Returns(release.Task);
        var oldRequest = removeCurrent
            ? _service.RemoveFromQueueAsync(_testSongs[0])
            : _service.ClearQueueAsync();

        await _service.PlayAsync(_testSongs[3]);
        release.SetResult();
        await oldRequest;

        _service.CurrentTrack.Should().Be(_testSongs[3]);
        _service.PlaybackQueue.Should().Equal(_testSongs[3].Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PlaybackEnded_WhenFinalizationIsDelayed_DoesNotAdvanceAfterNewRequestOrDisposal(bool dispose)
    {
        _library.Writer.StartListenSessionAsync(_testSongs[0].Id, Arg.Any<PlaybackContext>()).Returns(101L);
        await _service.PlayAsync(_testSongs);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _library.Writer.FinalizeListenSessionAsync(101L, Arg.Any<TimeSpan>(), PlaybackEndReason.Finished)
            .Returns(release.Task);
        _audioPlayer.PlaybackEnded += Raise.Event<Action>();
        var disposal = dispose ? _service.DisposeAsync().AsTask() : Task.CompletedTask;
        if (!dispose) await _service.PlayQueueItemAsync(3);
        _audioPlayer.ClearReceivedCalls();

        release.SetResult();
        await disposal;
        await _service.FlushPendingFinalizationAsync();

        await _audioPlayer.DidNotReceive().LoadAsync(Arg.Any<Song>());
        await _audioPlayer.DidNotReceive().PlayAsync();
        if (!dispose) _service.CurrentTrack.Should().Be(_testSongs[3]);
    }

    [Fact]
    public async Task DisposeAsync_WaitsForLateListenSessionAndFinalizesIt()
    {
        var release = new TaskCompletionSource<long?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _library.Writer.StartListenSessionAsync(_testSongs[0].Id, Arg.Any<PlaybackContext>()).Returns(release.Task);
        var playback = _service.PlayAsync(_testSongs[0]);
        var disposal = _service.DisposeAsync().AsTask();
        disposal.IsCompleted.Should().BeFalse();

        release.SetResult(42);
        await Task.WhenAll(playback, disposal);

        await _library.Writer.Received(1).FinalizeListenSessionAsync(42, TimeSpan.Zero, PlaybackEndReason.PausedAndAbandoned);
        await _audioPlayer.DidNotReceive().LoadAsync(Arg.Any<Song>());
    }

    [Fact]
    public async Task PlayTransientFileAsync_AfterNormalizedTrack_ResetsReplayGain()
    {
        _settingsService.GetVolumeNormalizationEnabledAsync().Returns(true);
        _testSongs[0].ReplayGainTrackGain = -6;
        await _service.PlayAsync(_testSongs[0]);
        const string path = "C:\\temp\\external.mp3";
        _metadataService.ExtractMetadataAsync(path).Returns(new SongFileMetadata { Title = "External" });

        await _service.PlayTransientFileAsync(path);

        Received.InOrder(() =>
        {
            _audioPlayer.SetReplayGainAsync(-6);
            _audioPlayer.SetReplayGainAsync(0);
        });
    }

    [Fact]
    public async Task SavePlaybackStateAsync_WhenStopped_PreservesQueuePosition()
    {
        await _service.InitializeAsync();
        await _service.PlayAsync(_testSongs, 3);
        await _service.StopAsync();

        await _service.SavePlaybackStateAsync();

        await _settingsService.Received().SavePlaybackStateAsync(Arg.Is<PlaybackState>(state =>
            state.CurrentTrackId == null && state.CurrentPlaybackQueueIndex == 3));
    }

    [Fact]
    public async Task InitializeAsync_WhenDisposedDuringSessionFileRead_DoesNotTouchAudio()
    {
        var release = new TaskCompletionSource<PlaybackState?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _settingsService.GetPlaybackStateAsync().Returns(release.Task);
        var initialization = _service.InitializeAsync();
        var disposal = _service.DisposeAsync().AsTask();
        release.SetResult(null);

        await Task.WhenAll(initialization, disposal);

        await _audioPlayer.DidNotReceive().SetVolumeAsync(Arg.Any<double>());
        _audioPlayer.DidNotReceive().ApplyEqualizerSettings(Arg.Any<EqualizerSettings>());
    }

    [Fact]
    public async Task PlayPauseAsync_AfterSessionRestore_StartsListenBeforePlaying()
    {
        _settingsService.GetRestorePlaybackStateEnabledAsync().Returns(true);
        _settingsService.GetPlaybackStateAsync().Returns(new PlaybackState
        {
            PlaybackQueueTrackIds = [_testSongs[0].Id],
            CurrentTrackId = _testSongs[0].Id
        });
        _library.Writer.StartListenSessionAsync(_testSongs[0].Id, Arg.Any<PlaybackContext>()).Returns(52L);
        await _service.InitializeAsync();
        _service.CurrentListenHistoryId.Should().BeNull();

        await _service.PlayPauseAsync();

        _service.CurrentListenHistoryId.Should().Be(52L);
        Received.InOrder(() =>
        {
            _library.Writer.StartListenSessionAsync(_testSongs[0].Id, Arg.Any<PlaybackContext>());
            _audioPlayer.PlayAsync();
        });
    }

    [Fact]
    public async Task ReplayGain_WhenDisabledDuringDatabaseRead_DoesNotApplyLateGain()
    {
        var release = new TaskCompletionSource<Song?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _settingsService.GetVolumeNormalizationEnabledAsync().Returns(true);
        _library.Reader.GetSongByIdAsync(_testSongs[0].Id).Returns(Task.FromResult<Song?>(_testSongs[0]), release.Task);
        var playback = _service.PlayAsync(_testSongs[0]);

        _settingsService.VolumeNormalizationEnabledChanged += Raise.Event<Action<bool>>(false);
        release.SetResult(new Song { Id = _testSongs[0].Id, ReplayGainTrackGain = -6 });
        await playback;
        await _service.FlushPendingFinalizationAsync();

        await _audioPlayer.Received(1).SetReplayGainAsync(0);
        await _audioPlayer.DidNotReceive().SetReplayGainAsync(-6);
    }

    [Fact]
    public async Task AddTransientFilesToQueueAsync_WhenClearedDuringMetadataRead_DoesNotRepopulateQueue()
    {
        const string path = "C:\\temp\\late.mp3";
        var release = new TaskCompletionSource<SongFileMetadata>(TaskCreationOptions.RunContinuationsAsynchronously);
        _metadataService.ExtractMetadataAsync(path).Returns(release.Task);
        var adding = _service.AddTransientFilesToQueueAsync([path]);

        await _service.ClearQueueAsync();
        release.SetResult(new SongFileMetadata { Title = "Late" });
        await adding;

        _service.PlaybackQueue.Should().BeEmpty();
        _service.CurrentTrack.Should().BeNull();
        await _audioPlayer.DidNotReceive().LoadAsync(Arg.Any<Song>());
    }

    [Fact]
    public async Task AddTransientFilesToQueueAsync_WithConcurrentSamePath_AddsOneTrack()
    {
        const string path = "C:\\temp\\same.mp3";
        var release = new TaskCompletionSource<SongFileMetadata>(TaskCreationOptions.RunContinuationsAsynchronously);
        _metadataService.ExtractMetadataAsync(path).Returns(release.Task, Task.FromResult(new SongFileMetadata { Title = "First" }));
        var first = _service.AddTransientFilesToQueueAsync([path]);
        await _service.AddTransientFilesToQueueAsync([path]);

        release.SetResult(new SongFileMetadata { Title = "Late" });
        await first;

        _service.PlaybackQueue.Should().ContainSingle();
        _service.CurrentTrack!.Title.Should().Be("First");
    }

    [Fact]
    public async Task SetVolumeAsync_PersistsTheVolumeAcceptedByTheAudioPlayer()
    {
        _audioPlayer.Volume.Returns(1.0);

        await _service.SetVolumeAsync(2.0);

        await _settingsService.Received(1).SaveVolumeAsync(1.0);
        await _settingsService.DidNotReceive().SaveVolumeAsync(2.0);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    public async Task SetPlaybackRateAsync_IgnoresInvalidRates(double rate)
    {
        await _service.SetPlaybackRateAsync(rate);

        await _audioPlayer.DidNotReceive().SetPlaybackRateAsync(Arg.Any<double>());
        _service.PlaybackRate.Should().Be(1.0);
    }

    [Fact]
    public async Task NextAsync_AfterPlayingTransientFile_StopsPlayback()
    {
        const string filePath = "C:\\temp\\transient.mp3";
        _metadataService.ExtractMetadataAsync(filePath).Returns(new SongFileMetadata { Title = "Transient" });
        await _service.InitializeAsync();

        await _service.PlayTransientFileAsync(filePath);

        _service.PlaybackQueue.Should().BeEmpty();

        await _service.NextAsync();

        _service.CurrentTrack.Should().BeNull();
        await _audioPlayer.Received(1).StopAsync();
    }

}
