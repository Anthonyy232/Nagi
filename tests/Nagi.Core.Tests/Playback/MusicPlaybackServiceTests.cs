using FluentAssertions;
using Microsoft.Extensions.Logging;
using Nagi.Core.Library;
using Nagi.Core.Metadata;
using Nagi.Core.MusicServers;
using Nagi.Core.Platform;
using Nagi.Core.Playback;
using Nagi.Core.Playlists;
using Nagi.Core.Settings;
using Nagi.Core.Tests.Utils;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Nagi.Core.Tests.Playback;

public partial class MusicPlaybackServiceTests : IAsyncDisposable
{
    private readonly IAudioPlayer _audioPlayer;
    private readonly LibraryFakes _library;
    private readonly ILogger<MusicPlaybackService> _logger;
    private readonly IMetadataService _metadataService;
    private readonly IFileSystemService _fileSystem = Substitute.For<IFileSystemService>();
    private readonly IMusicServerService _musicServers = Substitute.For<IMusicServerService>();
    private readonly ISmartPlaylistService _smartPlaylists = Substitute.For<ISmartPlaylistService>();

    private readonly MusicPlaybackService _service;

    private readonly SettingsService _settingsService;
    private readonly IPlaybackStateStore _playbackStateStore = Substitute.For<IPlaybackStateStore>();

    private readonly ISystemMediaControls _mediaControls = Substitute.For<ISystemMediaControls>();

    private readonly List<Song> _testSongs;

    public MusicPlaybackServiceTests()
    {
        _settingsService = TestSettings.Create();
        _audioPlayer = Substitute.For<IAudioPlayer>();
        _library = new LibraryFakes();
        _metadataService = Substitute.For<IMetadataService>();
        _logger = Substitute.For<ILogger<MusicPlaybackService>>();

        _playbackStateStore.LoadAsync().Returns((PlaybackState?)null);

        _service = new MusicPlaybackService(
            _settingsService, _playbackStateStore,
            _audioPlayer,
            _mediaControls, new ImmediateDispatcher(),
            _library.Reader, _smartPlaylists, _library.Listening,
            _metadataService,
            _fileSystem,
            _musicServers,
            _logger);

        _testSongs = CreateTestSongs(5);

        foreach (var song in _testSongs)
        {
            _library.Reader.GetSongByIdAsync(song.Id).Returns(song);
        }
        _library.Reader.GetSongsByIdsAsync(Arg.Any<IEnumerable<Guid>>()).Returns(call =>
        {
            var ids = call.Arg<IEnumerable<Guid>>().ToHashSet();
            return (IReadOnlyDictionary<Guid, Song>)_testSongs.Where(s => ids.Contains(s.Id)).ToDictionary(s => s.Id);
        });
        _library.Reader.GetQueueSongsAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            var ids = call.Arg<IEnumerable<Guid>>().ToHashSet();
            return (IReadOnlyDictionary<Guid, Song>)_testSongs.Where(s => ids.Contains(s.Id)).ToDictionary(s => s.Id);
        });
    }

    public ValueTask DisposeAsync() => _service.DisposeAsync();

    private static IEnumerable<Guid> Ids(IEnumerable<Song> songs) => songs.Select(song => song.Id);

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
        await _service.AddRangeToQueueAsync([_testSongs[0].Id, _testSongs[1].Id]);
        await _service.AddRangeToQueueAsync([_testSongs[1].Id]);
        await _service.AddRangeToQueueAsync(new[]
            { _testSongs[0].Id, _testSongs[1].Id, _testSongs[2].Id, _testSongs[2].Id, Guid.Empty });

        _service.PlayOrder.Should().Equal(_testSongs.Take(3).Select(song => song.Id));
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

        _service.CurrentTrack.Should().BeSameAs(selected);
        if (shuffle) _service.PlayOrder.Should().Equal(selected.Id, first.Id);
        else _service.PlayOrder.Should().Equal(first.Id, selected.Id);
        await _service.SetShuffleAsync(false);
        _service.PlayOrder.Should().Equal(first.Id, selected.Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MoveQueueItem_PreservesPlaybackAndSavedOrder(bool shuffle)
    {
        await _service.InitializeAsync();
        await _service.PlayAsync(Ids(_testSongs));
        await _service.SetShuffleAsync(shuffle);
        var current = _service.CurrentTrack!;
        var original = Ids(_testSongs).ToArray();
        var order = _service.PlayOrder.ToList();
        var others = order.Where(id => id != current.Id).ToList();
        var moved = others[^1];
        var before = others[0];
        order.Remove(moved);
        order.Insert(order.IndexOf(before), moved);
        _audioPlayer.ClearReceivedCalls();

        _service.MoveQueueItem(moved, before).Should().BeTrue();

        _service.PlayOrder.Should().Equal(order);
        _service.CurrentTrack.Should().BeSameAs(current);
        _service.UpcomingQueue[0].Should().Be(current.Id);
        await _audioPlayer.DidNotReceive().LoadAsync(Arg.Any<Song>());
        await _audioPlayer.DidNotReceive().StopAsync();
        PlaybackState? saved = null;
        _playbackStateStore.SaveAsync(Arg.Do<PlaybackState>(s => saved = s)).Returns(Task.CompletedTask);
        await _service.SavePlaybackStateAsync();
        (shuffle ? saved!.ShuffledQueueTrackIds : saved!.PlaybackQueueTrackIds).Should().Equal(order);

        _service.MoveQueueItem(moved, null);
        _service.PlayOrder.Last().Should().Be(moved);
        if (shuffle)
        {
            await _service.SetShuffleAsync(false);
            _service.PlayOrder.Should().Equal(original);
        }
    }

    [Fact]
    public async Task UpcomingQueueAndNextTrack_FollowWhatNextPlays_IncludingTheWrapWhileRepeatingAll()
    {
        await _service.InitializeAsync();
        await _service.PlayAsync(Ids(_testSongs), _testSongs.Count - 1);

        _service.UpcomingQueue.Should().Equal(_testSongs[^1].Id);
        _service.NextTrackId.Should().BeNull();

        await _service.SetRepeatModeAsync(RepeatMode.RepeatAll);

        _service.UpcomingQueue.Should().Equal(Ids(_testSongs.TakeLast(1).Concat(_testSongs.SkipLast(1))));
        _service.NextTrackId.Should().Be(_testSongs[0].Id);
        await _service.NextAsync();
        _service.CurrentTrack.Should().BeSameAs(_testSongs[0]);
    }

    [Fact]
    public async Task PlayNextAsync_WithSeveralSongs_PlaysThemNextInTheOrderGiven()
    {
        await _service.InitializeAsync();
        await _service.PlayAsync(Ids(_testSongs));

        await _service.PlayNextAsync([_testSongs[4].Id, _testSongs[2].Id, _testSongs[3].Id]);

        _service.PlayOrder.Should().Equal(Ids([_testSongs[0], _testSongs[4], _testSongs[2], _testSongs[3], _testSongs[1]]));
    }

    [Fact]
    public async Task MoveQueueItem_KeepsTheCurrentSongFirstInTheUpcomingQueue()
    {
        await _service.InitializeAsync();
        await _service.PlayAsync(Ids(_testSongs), 1);
        var current = _testSongs[1].Id;

        _service.MoveQueueItem(current, null).Should().BeFalse();
        _service.MoveQueueItem(_testSongs[3].Id, current).Should().BeFalse();

        _service.PlayOrder.Should().Equal(Ids(_testSongs));
    }

    [Fact]
    public async Task MoveQueueItem_ToTheEndOfAWrappingQueue_PlacesTheSongLastBeforeTheCurrentOneComesRound()
    {
        await _service.InitializeAsync();
        await _service.PlayAsync(Ids(_testSongs), 2);
        await _service.SetRepeatModeAsync(RepeatMode.RepeatAll);
        var moved = _testSongs[3].Id;

        _service.MoveQueueItem(moved, null).Should().BeTrue();

        _service.UpcomingQueue[^1].Should().Be(moved);
        _service.UpcomingQueue[0].Should().Be(_testSongs[2].Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemoveFromQueueAsync_AfterReorder_AdvancesInDisplayedOrder(bool shuffle)
    {
        await _service.InitializeAsync();
        await _service.PlayAsync(Ids(_testSongs));
        await _service.SetShuffleAsync(shuffle);
        var current = _service.CurrentTrack!.Id;
        var chosenNext = _testSongs[^1].Id;
        await _service.PlayNextAsync([chosenNext]);
        var expected = _service.PlayOrder.Where(id => id != current).ToArray();

        await _service.RemoveFromQueueAsync(current);

        _service.CurrentTrack!.Id.Should().Be(chosenNext);
        _service.PlayOrder.Should().Equal(expected);
    }

    [Fact]
    public async Task OpenedFile_GetsItsTagsCleanedUpLikeALibrarySong()
    {
        const string path = "C:\\temp\\opened.mp3";
        _metadataService.ExtractMetadataAsync(path).Returns(new SongFileMetadata
        {
            Title = " Opened ",
            Artists = [" Artist "],
            Album = " Album ",
            Genres = [" Jazz ", "jazz"]
        });
        await _service.InitializeAsync();

        await _service.PlayTransientFileAsync(path);

        var song = _service.CurrentTrack!;
        song.Title.Should().Be("Opened");
        song.Album!.Title.Should().Be("Album");
        song.Album.PrimaryArtistName.Should().Be("Artist");
        song.Genres.Select(g => g.Name).Should().Equal("Jazz");
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
        var transientId = _service.PlayOrder.Single();
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
        var current = _testSongs[0].Id;
        var target = _service.PlayOrder.First(id => id != current);
        var last = _service.PlayOrder.Last(id => id != current);
        var expected = _service.PlayOrder.ToList();
        expected.Remove(last);
        expected.Insert(expected.IndexOf(target), last);

        _service.MoveQueueItem(last, target).Should().BeTrue();
        _service.MoveQueueItem(last, Guid.NewGuid()).Should().BeFalse();
        _service.MoveQueueItem(Guid.NewGuid(), target).Should().BeFalse();
        _service.MoveQueueItem(last, last).Should().BeFalse();

        _service.PlayOrder.Should().Equal(expected);
        _service.CurrentTrack!.Id.Should().Be(current);
        _service.UpcomingQueue[0].Should().Be(current);
    }

    [Fact]
    public async Task PlayTransientFileAsync_ReplacesTheQueueWithTheFileAndPlaysIt()
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
        await _service.PlayAsync(Ids(_testSongs));
        _audioPlayer.ClearReceivedCalls();

        await _service.PlayTransientFileAsync(filePath);

        _service.CurrentTrack.Should().NotBeNull();
        _service.CurrentTrack!.Title.Should().Be("Transient Song");
        _service.PlayOrder.Should().Equal(_service.CurrentTrack.Id);
        _service.CurrentTrack!.ArtistName.Should().Be("Temp Artist");
        _service.CurrentTrack!.Lyrics.Should().Be("Embedded lyrics");
        _service.CurrentTrack!.LrcFilePath.Should().Be("C:\\temp\\transient.lrc");
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
        await _service.PlayAsync(Ids(_testSongs));
        _audioPlayer.ClearReceivedCalls();
        _library.ClearReceivedCalls();

        await _service.AddTransientFilesToQueueAsync([firstPath, secondPath]);

        _service.PlayOrder.Should().HaveCount(_testSongs.Count + 2);
        var externalIds = _service.PlayOrder.TakeLast(2).ToList();
        var tracks = await _service.GetQueueSongsAsync(externalIds);
        externalIds.Select(id => tracks[id].Title).Should().ContainInOrder("First", "Second");

        await _service.SavePlaybackStateAsync();
        await _playbackStateStore.Received().SaveAsync(Arg.Is<PlaybackState>(state =>
            state.PlaybackQueueTrackIds.SequenceEqual(_testSongs.Select(song => song.Id))));

        await _service.PlayQueueItemAsync(externalIds[0]);

        await _audioPlayer.Received(1).LoadAsync(Arg.Is<Song>(song => song.FilePath == firstPath));
        await _library.Listening.DidNotReceive().StartListenSessionAsync(
            externalIds[0], Arg.Any<PlaybackContext>());
    }

    [Fact]
    public async Task AddTransientFilesToQueueAsync_WhenStoppedWithAQueue_PlaysTheDroppedFile()
    {
        const string path = "C:\\temp\\dropped.mp3";
        _metadataService.ExtractMetadataAsync(path).Returns(new SongFileMetadata { Title = "Dropped" });
        await _service.InitializeAsync();
        await _service.PlayAsync(Ids(_testSongs));
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

        var tracks = await _service.GetQueueSongsAsync(_service.PlayOrder);
        _service.PlayOrder.Select(id => tracks[id].Title).Should().Equal("c.flac", "b.mp3");
    }

    private class EventTracker
    {
        public EventTracker(MusicPlaybackService service)
        {
            service.ShuffleModeChanged += () => ShuffleModeChangedCount++;
            service.RepeatModeChanged += () => RepeatModeChangedCount++;
            service.QueueChanged += () => QueueChangedCount++;
        }

        public int ShuffleModeChangedCount { get; private set; }
        public int RepeatModeChangedCount { get; private set; }
        public int QueueChangedCount { get; private set; }
    }

    [Fact]
    public async Task InitializeAsync_WithDuplicateSavedQueue_RestoresUniqueTracksInOriginalOrder()
    {
        var first = _testSongs[0];
        var second = _testSongs[1];
        _settingsService.Set(CoreSettings.RestorePlaybackState, true);
        _playbackStateStore.LoadAsync().Returns(new PlaybackState
        {
            PlaybackQueueTrackIds = [first.Id, Guid.Empty, first.Id, second.Id],
            CurrentPlaybackQueueIndex = 3
        });

        await _service.InitializeAsync();

        _service.PlayOrder.Should().Equal(first.Id, second.Id);
        _service.UpcomingQueue[0].Should().Be(second.Id);
        await _service.PlayPauseAsync();
        _service.CurrentTrack.Should().BeSameAs(second);
    }

    [Fact]
    public async Task InitializeAsync_WithSongsNoLongerInTheLibrary_DropsThemAndKeepsTheShuffleOrder()
    {
        var (a, b, c) = (_testSongs[0].Id, _testSongs[1].Id, _testSongs[2].Id);
        var removed = Guid.NewGuid();
        _settingsService.Set(CoreSettings.RestorePlaybackState, true);
        _settingsService.Set(CoreSettings.IsShuffleEnabled, true);
        _playbackStateStore.LoadAsync().Returns(new PlaybackState
        {
            PlaybackQueueTrackIds = [a, removed, b, c],
            ShuffledQueueTrackIds = [c, removed, a, b],
            CurrentTrackId = removed
        });

        await _service.InitializeAsync();

        _service.PlayOrder.Should().Equal(c, a, b);
        await _service.PlayPauseAsync();
        _service.CurrentTrack!.Id.Should().Be(c);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InitializeAsync_WithInvalidShuffleOfSameLength_RebuildsPermutation(bool duplicate)
    {
        var ids = _testSongs.Select(song => song.Id).ToList();
        var shuffle = ids.ToList();
        shuffle[^1] = duplicate ? ids[0] : Guid.NewGuid();
        _settingsService.Set(CoreSettings.RestorePlaybackState, true);
        _settingsService.Set(CoreSettings.IsShuffleEnabled, true);
        _playbackStateStore.LoadAsync().Returns(new PlaybackState
        {
            PlaybackQueueTrackIds = ids,
            ShuffledQueueTrackIds = shuffle,
            CurrentTrackId = ids[0]
        });

        await _service.InitializeAsync();

        _service.PlayOrder.Should().OnlyHaveUniqueItems();
        _service.PlayOrder.Should().BeEquivalentTo(ids);
        _service.UpcomingQueue[0].Should().Be(ids[0]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InitializeAsync_WithOutOfRangeSavedPosition_RemainsPlayable(bool shuffle)
    {
        var ids = _testSongs.Select(song => song.Id).ToList();
        _settingsService.Set(CoreSettings.RestorePlaybackState, true);
        _settingsService.Set(CoreSettings.IsShuffleEnabled, shuffle);
        _playbackStateStore.LoadAsync().Returns(new PlaybackState
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
    }

    [Fact]
    public async Task InitializeAsync_WithCurrentTrackOutsideSavedQueue_DoesNotLoadUnqueuedTrack()
    {
        var first = _testSongs[0];
        var unqueued = _testSongs[1];
        _settingsService.Set(CoreSettings.RestorePlaybackState, true);
        _playbackStateStore.LoadAsync().Returns(new PlaybackState
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
        _settingsService.Set(CoreSettings.Volume, 0.75);
        _settingsService.Set(CoreSettings.IsMuted, true);
        _settingsService.Set(CoreSettings.IsShuffleEnabled, true);
        _settingsService.Set(CoreSettings.RepeatMode, RepeatMode.RepeatAll);

        await _service.InitializeAsync();

        await _audioPlayer.Received(1).SetVolumeAsync(0.75);
        await _audioPlayer.Received(1).SetMuteAsync(true);
        _service.IsShuffleEnabled.Should().BeTrue();
        _service.CurrentRepeatMode.Should().Be(RepeatMode.RepeatAll);
    }

    [Fact]
    public async Task InitializeAsync_WhenSettingsServiceThrows_FallsBackToDefaultSettings()
    {
        var settings = Substitute.For<ISettingsService>();
        settings.Get(CoreSettings.Volume).Throws(new InvalidOperationException("Config file corrupted"));
        settings.Get(CoreSettings.IsMuted).Returns(true);
        settings.Get(CoreSettings.IsShuffleEnabled).Returns(true);
        settings.Get(CoreSettings.RepeatMode).Returns(RepeatMode.RepeatAll);
        using var service = new MusicPlaybackService(settings, _playbackStateStore, _audioPlayer, _mediaControls, new ImmediateDispatcher(),
            _library.Reader, _smartPlaylists, _library.Listening, _metadataService, _fileSystem, _musicServers, _logger);

        await service.InitializeAsync();

        await _audioPlayer.Received(1).SetVolumeAsync(0.5);
        await _audioPlayer.Received(1).SetMuteAsync(false);
        service.IsShuffleEnabled.Should().BeFalse();
        service.CurrentRepeatMode.Should().Be(RepeatMode.Off);
        service.PlayOrder.Should().BeEmpty();
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
        _settingsService.Set(CoreSettings.RestorePlaybackState, true);
        _playbackStateStore.LoadAsync().Returns(savedState);
        await _service.InitializeAsync();

        _service.PlayOrder.Should().BeEquivalentTo(_testSongs.Select(s => s.Id));
        _service.CurrentTrack.Should().Be(_testSongs[1]);
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
        _settingsService.Set(CoreSettings.RestorePlaybackState, true);
        _playbackStateStore.LoadAsync().Returns(savedState);

        _library.Reader.GetSongByIdAsync(_testSongs[1].Id).Returns((Song?)null);

        await _service.InitializeAsync();

        _service.PlayOrder.Should().BeEquivalentTo(songIds);
        _service.CurrentTrack.Should().BeNull();
        _service.UpcomingQueue[0].Should().Be(songIds[0]);
        await _audioPlayer.DidNotReceive().LoadAsync(Arg.Any<Song>());
    }

    [Fact]
    public async Task PlayAsync_WithSongList_ReplacesQueueAndPlaysFromStartIndex()
    {
        await _service.InitializeAsync();

        await _service.PlayAsync(Ids(_testSongs), 2);

        _service.PlayOrder.Should().BeEquivalentTo(_testSongs.Select(s => s.Id), options => options.WithStrictOrdering());
        _service.CurrentTrack.Should().Be(_testSongs[2]);
        await _audioPlayer.Received(1).LoadAsync(_testSongs[2]);
        await _audioPlayer.Received(1).PlayAsync();
    }

    [Fact]
    public async Task PlayAsync_WithEmptyList_StopsAndClearsQueue()
    {
        await _service.InitializeAsync();
        await _service.PlayAsync(Ids(_testSongs));

        await _service.PlayAsync(Array.Empty<Guid>());

        await _audioPlayer.Received(1).StopAsync();
        _service.PlayOrder.Should().BeEmpty();
        _service.CurrentTrack.Should().BeNull();
    }

    [Fact]
    public async Task PlayAsync_WithInvalidStartIndex_PlaysFromBeginning()
    {
        await _service.InitializeAsync();

        await _service.PlayAsync(Ids(_testSongs), 99);

        _service.CurrentTrack.Should().Be(_testSongs[0]);
        await _audioPlayer.Received(1).LoadAsync(_testSongs[0]);
    }

    [Fact]
    public async Task PlayPauseAsync_WhenPlaying_PausesPlayback()
    {
        await _service.InitializeAsync();
        await _service.PlayAsync(_testSongs[0].Id);
        _audioPlayer.IsPlaying.Returns(true);

        await _service.PlayPauseAsync();

        await _audioPlayer.Received(1).PauseAsync();
    }

    [Fact]
    public async Task PlayPauseAsync_WhenStoppedWithQueue_PlaysFromCurrentIndex()
    {
        await _service.InitializeAsync();

        await _service.PlayAsync(Ids(_testSongs), 2);
        await _service.StopAsync();
        _audioPlayer.IsPlaying.Returns(false);
        _audioPlayer.ClearReceivedCalls();

        await _service.PlayPauseAsync();

        _service.CurrentTrack.Should().Be(_testSongs[2]);
        await _audioPlayer.Received(1).LoadAsync(_testSongs[2]);
    }

    [Fact]
    public async Task RepeatOne_ReplaysTheTrackWhenItEndsButNextStillMovesOn()
    {
        await _service.InitializeAsync();
        await _service.SetRepeatModeAsync(RepeatMode.RepeatOne);
        await _service.PlayAsync(Ids(_testSongs), _testSongs.Count - 2);

        await _service.NextAsync();
        _service.CurrentTrack.Should().Be(_testSongs[^1]);
        await _service.NextAsync();
        _service.CurrentTrack.Should().Be(_testSongs[0]);

        _audioPlayer.ClearReceivedCalls();
        _audioPlayer.PlaybackEnded += Raise.Event<Action>();
        await _service.FlushPendingFinalizationAsync();
        _service.CurrentTrack.Should().Be(_testSongs[0]);
        await _audioPlayer.Received(1).LoadAsync(_testSongs[0]);
    }

    [Fact]
    public async Task TrackChanged_IsRaisedOncePerTrackWithItsListenAlreadyStarted()
    {
        _library.Listening.StartListenSessionAsync(_testSongs[1].Id, Arg.Any<PlaybackContext>()).Returns(42L);
        await _service.InitializeAsync();
        var seen = new List<(Guid? Track, long? Listen)>();
        _service.TrackChanged += () => seen.Add((_service.CurrentTrack?.Id, _service.CurrentListenHistoryId));

        await _service.PlayAsync(Ids(_testSongs), 1);
        _audioPlayer.MediaOpened += Raise.Event<Action>();

        seen.Should().Equal((_testSongs[1].Id, 42L));
    }

    [Fact]
    public async Task DisposeAsync_AfterDispose_StillEndsTheListen()
    {
        _library.Listening.StartListenSessionAsync(Arg.Any<Guid>(), Arg.Any<PlaybackContext>()).Returns(7L);
        await _service.InitializeAsync();
        await _service.PlayAsync(_testSongs[0].Id);

        _service.Dispose();
        await _service.DisposeAsync();

        await _library.Listening.Received(1).FinalizeListenSessionAsync(7L, Arg.Any<TimeSpan>(), PlaybackEndReason.PausedAndAbandoned);
    }

    [Fact]
    public async Task StopAfterTrack_StopsWhenTheTrackEndsButNextStillMovesOn()
    {
        await _service.InitializeAsync();
        await _service.PlayAsync(Ids(_testSongs));
        _service.ScheduleStop(PlaybackStopMode.AfterTrack);

        await _service.NextAsync();
        _service.CurrentTrack.Should().Be(_testSongs[1]);
        _service.StopMode.Should().Be(PlaybackStopMode.AfterTrack);

        _audioPlayer.PlaybackEnded += Raise.Event<Action>();
        await _service.FlushPendingFinalizationAsync();
        _service.CurrentTrack.Should().BeNull();
        _service.StopMode.Should().Be(PlaybackStopMode.Off);
    }

    [Fact]
    public async Task NextAsync_AtEndOfQueueWithRepeatAll_PlaysFirstSong()
    {
        await _service.InitializeAsync();
        await _service.SetRepeatModeAsync(RepeatMode.RepeatAll);
        await _service.PlayAsync(Ids(_testSongs), _testSongs.Count - 1);

        await _service.NextAsync();

        _service.CurrentTrack.Should().Be(_testSongs[0]);
        await _audioPlayer.Received(1).LoadAsync(_testSongs[0]);
    }

    [Fact]
    public async Task NextAsync_AtEndOfQueueWithNoRepeat_StopsPlayback()
    {
        await _service.InitializeAsync();
        await _service.PlayAsync(Ids(_testSongs), _testSongs.Count - 1);

        await _service.NextAsync();

        _service.CurrentTrack.Should().BeNull();
        await _audioPlayer.Received(1).StopAsync();
    }

    [Fact]
    public async Task PreviousAsync_WithRepeatOne_RestartsAPlayingTrackAndOtherwiseGoesBack()
    {
        await _service.InitializeAsync();
        await _service.SetRepeatModeAsync(RepeatMode.RepeatOne);
        await _service.PlayAsync(Ids(_testSongs), 1);
        _audioPlayer.CurrentPosition.Returns(TimeSpan.FromSeconds(10));
        _audioPlayer.ClearReceivedCalls();

        await _service.PreviousAsync();

        _service.CurrentTrack.Should().Be(_testSongs[1]);
        await _audioPlayer.Received(1).SeekAsync(TimeSpan.Zero);
        await _audioPlayer.DidNotReceive().LoadAsync(Arg.Any<Song>());

        _audioPlayer.CurrentPosition.Returns(TimeSpan.FromSeconds(1));
        await _service.PreviousAsync();
        _service.CurrentTrack.Should().Be(_testSongs[0]);
    }

    [Fact]
    public async Task PreviousAsync_AtStartOfQueueWithRepeatAll_PlaysLastSong()
    {
        await _service.InitializeAsync();
        await _service.SetRepeatModeAsync(RepeatMode.RepeatAll);
        await _service.PlayAsync(Ids(_testSongs));
        _audioPlayer.CurrentPosition.Returns(TimeSpan.FromSeconds(1));

        await _service.PreviousAsync();

        _service.CurrentTrack.Should().Be(_testSongs.Last());
        await _audioPlayer.Received(1).LoadAsync(_testSongs.Last());
    }

    [Fact]
    public async Task PlayAsync_CreatesListenSessionAndSetsContext()
    {
        await _service.InitializeAsync();
        _library.Listening.StartListenSessionAsync(Arg.Any<Guid>(), Arg.Any<PlaybackContext>()).Returns(100L);

        await _service.PlayAsync(_testSongs[0].Id);

        _service.CurrentListenHistoryId.Should().Be(100L);
        await _library.Listening.Received(1).StartListenSessionAsync(
            _testSongs[0].Id,
            Arg.Is<PlaybackContext>(c => c != null && c.Type == PlaybackContextType.Library && c.ContextId == null));
    }

    [Fact]
    public async Task PlayAsync_RaisesTrackChanged_AfterListenSessionIdIsSet()
    {
        await _service.InitializeAsync();
        _library.Listening.StartListenSessionAsync(Arg.Any<Guid>(), Arg.Any<PlaybackContext>()).Returns(150L);

        long? listenHistoryIdSeenByEvent = null;
        _service.TrackChanged += () => listenHistoryIdSeenByEvent = _service.CurrentListenHistoryId;

        _audioPlayer.When(x => x.LoadAsync(Arg.Any<Song>())).Do(_ =>
        {
            _audioPlayer.MediaOpened += Raise.Event<Action>();
        });

        await _service.PlayAsync(_testSongs[0].Id);

        listenHistoryIdSeenByEvent.Should().Be(150L,
            "TrackChanged consumers (e.g. presence) must always see the active listen session ID");
    }

    [Fact]
    public async Task StopAsync_WithActiveSession_FinalizesSessionAsPausedAndAbandoned()
    {
        await _service.InitializeAsync();
        _library.Listening.StartListenSessionAsync(Arg.Any<Guid>(), Arg.Any<PlaybackContext>()).Returns(200L);
        await _service.PlayAsync(_testSongs[0].Id);
        _audioPlayer.CurrentPosition.Returns(TimeSpan.FromSeconds(30));

        await _service.StopAsync();
        await _service.FlushPendingFinalizationAsync();

        _service.CurrentListenHistoryId.Should().BeNull();
        await _library.Listening.Received(1).FinalizeListenSessionAsync(
            200L,
            TimeSpan.FromSeconds(30),
            PlaybackEndReason.PausedAndAbandoned);
    }

    [Fact]
    public async Task NextAsync_WithActiveSession_FinalizesSessionAsSkipped()
    {
        await _service.InitializeAsync();
        _library.Listening.StartListenSessionAsync(Arg.Any<Guid>(), Arg.Any<PlaybackContext>()).Returns(300L);
        await _service.PlayAsync(Ids(_testSongs), 0);
        _audioPlayer.CurrentPosition.Returns(TimeSpan.FromSeconds(45));

        _library.Listening.StartListenSessionAsync(Arg.Any<Guid>(), Arg.Any<PlaybackContext>()).Returns(301L);

        await _service.NextAsync();
        await _service.FlushPendingFinalizationAsync();

        await _library.Listening.Received(1).FinalizeListenSessionAsync(
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
        _library.Listening.StartListenSessionAsync(Arg.Any<Guid>(), Arg.Any<PlaybackContext>()).Returns(400L);
        await _service.PlayAsync(Ids(_testSongs), 0);

        var duration = TimeSpan.FromMinutes(3);
        _audioPlayer.Duration.Returns(duration);
        _audioPlayer.CurrentPosition.Returns(duration);

        _audioPlayer.PlaybackEnded += Raise.Event<Action>();
        await _service.FlushPendingFinalizationAsync();

        await _library.Listening.Received(1).FinalizeListenSessionAsync(
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
    [InlineData(PlaybackContextType.SmartPlaylist)]
    public async Task PlayingACollection_StartsTheListenSessionWithThatCollectionAsContext(PlaybackContextType type)
    {
        await _service.InitializeAsync();
        var id = Guid.NewGuid();
        var songIds = _testSongs.Select(s => s.Id).ToList();
        _library.Listening.StartListenSessionAsync(Arg.Any<Guid>(), Arg.Any<PlaybackContext>()).Returns(500L);

        switch (type)
        {
            case PlaybackContextType.Album:
                _library.Reader.GetSongIdsAsync(new SongScope.Album(id), null, Arg.Any<SongSortOrder>()).Returns(songIds);
                await _service.PlayAlbumAsync(id);
                break;
            case PlaybackContextType.Artist:
                _library.Reader.GetSongIdsAsync(new SongScope.Artist(id), null, Arg.Any<SongSortOrder>()).Returns(songIds);
                await _service.PlayArtistAsync(id);
                break;
            case PlaybackContextType.Folder:
                _library.Reader.GetSongIdsAsync(new SongScope.Folder(id), null, Arg.Any<SongSortOrder>()).Returns(songIds);
                await _service.PlayFolderAsync(id);
                break;
            case PlaybackContextType.Genre:
                _library.Reader.GetSongIdsAsync(new SongScope.Genre(id), null, Arg.Any<SongSortOrder>()).Returns(songIds);
                await _service.PlayGenreAsync(id);
                break;
            case PlaybackContextType.SmartPlaylist:
                _smartPlaylists.GetMatchingSongIdsAsync(id, token: Arg.Any<CancellationToken>()).Returns(songIds);
                (await _service.PlaySmartPlaylistAsync(id)).Should().BeTrue();
                break;
            default:
                _library.Reader.GetSongIdsAsync(new SongScope.Playlist(id), null, Arg.Any<SongSortOrder>()).Returns(songIds);
                await _service.PlayPlaylistAsync(id);
                break;
        }

        await _library.Listening.Received(1).StartListenSessionAsync(
            _testSongs[0].Id, Arg.Is<PlaybackContext>(c => c != null && c.Type == type && c.ContextId == id));
        _service.CurrentListenHistoryId.Should().Be(500L);
    }

    [Fact]
    public async Task DisposeAsync_FinalizesActiveSessionAsPausedAndAbandoned()
    {
        await _service.InitializeAsync();
        _library.Listening.StartListenSessionAsync(Arg.Any<Guid>(), Arg.Any<PlaybackContext>()).Returns(600L);
        await _service.PlayAsync(_testSongs[0].Id);
        _audioPlayer.CurrentPosition.Returns(TimeSpan.FromSeconds(15));

        await _service.DisposeAsync();

        await _library.Listening.Received(1).FinalizeListenSessionAsync(
            600L,
            TimeSpan.FromSeconds(15),
            PlaybackEndReason.PausedAndAbandoned);
    }

    [Fact]
    public async Task PlayTransientFileAsync_StartsTransientSessionAndFinalizesPrevious()
    {
        await _service.InitializeAsync();
        _library.Listening.StartListenSessionAsync(Arg.Any<Guid>(), Arg.Any<PlaybackContext>()).Returns(100L);

        await _service.PlayAsync(_testSongs[0].Id);

        var filePath = "C:\\temp\\transient.mp3";
        var metadata = new SongFileMetadata { Title = "Transient Song", Artists = new List<string> { "Temp Artist" } };
        _metadataService.ExtractMetadataAsync(filePath).Returns(metadata);

        _audioPlayer.CurrentPosition.Returns(TimeSpan.FromSeconds(20));
        _audioPlayer.ClearReceivedCalls();
        _library.ClearReceivedCalls();

        await _service.PlayTransientFileAsync(filePath);
        await _service.FlushPendingFinalizationAsync();

        await _library.Listening.Received(1).FinalizeListenSessionAsync(
            100L,
            TimeSpan.FromSeconds(20),
            PlaybackEndReason.Skipped);

        _service.CurrentListenHistoryId.Should().BeNull();
        await _library.Listening.DidNotReceiveWithAnyArgs().StartListenSessionAsync(Guid.Empty, default!);
    }

    [Fact]
    public async Task PlayTransientFileAsync_WhenFileIsInTheLibrary_PlaysAndRecordsTheLibrarySong()
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
        _library.Reader.GetSongByIdAsync(mappedSong.Id).Returns(mappedSong);
        _library.Listening.StartListenSessionAsync(mappedSong.Id, Arg.Any<PlaybackContext>()).Returns(777L);

        await _service.PlayTransientFileAsync(filePath);

        _service.CurrentTrack.Should().BeSameAs(mappedSong);
        _service.CurrentListenHistoryId.Should().Be(777L);
        await _library.Listening.Received(1).StartListenSessionAsync(
            mappedSong.Id,
            Arg.Is<PlaybackContext>(c => c != null && c.Type == PlaybackContextType.Transient && c.ContextId == null));
    }

    [Fact]
    public async Task PlayQueueItemAsync_WhenSkippingDeletedSongs_FinalizesPreviousSessionCorrectly()
    {

        await _service.InitializeAsync();
        _library.Listening.StartListenSessionAsync(Arg.Any<Guid>(), Arg.Any<PlaybackContext>()).Returns(700L);
        await _service.PlayAsync(_testSongs[0].Id);

        _audioPlayer.CurrentPosition.Returns(TimeSpan.FromSeconds(15));

        await _service.AddRangeToQueueAsync([_testSongs[1].Id, _testSongs[2].Id]);

        _library.Reader.GetSongByIdAsync(_testSongs[1].Id).Returns((Song?)null);

        _library.Reader.GetSongByIdAsync(_testSongs[2].Id).Returns(_testSongs[2]);
        _library.Listening.StartListenSessionAsync(Arg.Any<Guid>(), Arg.Any<PlaybackContext>()).Returns(701L);

        await _service.PlayQueueItemAsync(_testSongs[1].Id);
        await _service.FlushPendingFinalizationAsync();

        await _library.Listening.Received(1).FinalizeListenSessionAsync(
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
        _library.Listening.StartListenSessionAsync(Arg.Any<Guid>(), Arg.Any<PlaybackContext>()).Returns(900L);
        await _service.PlayAsync(Ids(_testSongs), 1);

        _audioPlayer.CurrentPosition.Returns(TimeSpan.FromSeconds(2));

        _library.Listening.StartListenSessionAsync(Arg.Any<Guid>(), Arg.Any<PlaybackContext>()).Returns(901L);

        await _service.PreviousAsync();
        await _service.FlushPendingFinalizationAsync();

        await _library.Listening.Received(1).FinalizeListenSessionAsync(
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
        _library.Listening.StartListenSessionAsync(Arg.Any<Guid>(), Arg.Any<PlaybackContext>()).Returns(1000L);
        await _service.PlayAsync(Ids(_testSongs), 1);

        _audioPlayer.CurrentPosition.Returns(TimeSpan.FromSeconds(10));
        _library.ClearReceivedCalls();

        await _service.PreviousAsync();
        await _service.FlushPendingFinalizationAsync();

        await _library.Listening.DidNotReceiveWithAnyArgs().FinalizeListenSessionAsync(default, default, default);

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
        _library.Listening.StartListenSessionAsync(Arg.Any<Guid>(), Arg.Any<PlaybackContext>()).Returns(1500L);

        await _service.PlayAsync(_testSongs.Select(s => s.Id).ToList(), 0, null, context);

        await _library.Listening.Received(1).StartListenSessionAsync(
            _testSongs[0].Id,
            Arg.Is<PlaybackContext>(c => c != null && c.Type == PlaybackContextType.SmartPlaylist && c.ContextId == smartPlaylistId));
        _service.CurrentListenHistoryId.Should().Be(1500L);
    }

    [Fact]
    public async Task SetShuffleAsync_WhenEnabled_GeneratesShuffledQueue()
    {
        await _service.InitializeAsync();
        await _service.PlayAsync(Ids(_testSongs));
        var eventTracker = new EventTracker(_service);

        await _service.SetShuffleAsync(true);

        _service.IsShuffleEnabled.Should().BeTrue();
        _service.CurrentTrack.Should().Be(_testSongs[0]);
        _service.PlayOrder.Should().BeEquivalentTo(_testSongs.Select(s => s.Id));
        _settingsService.Get(CoreSettings.IsShuffleEnabled).Should().BeTrue();
        eventTracker.ShuffleModeChangedCount.Should().Be(1);

        eventTracker.QueueChangedCount.Should().Be(1);
    }

    [Fact]
    public async Task SetShuffleAsync_WhenDisabled_PlaysInTheQueueOrderAgain()
    {
        await _service.InitializeAsync();
        await _service.PlayAsync(Ids(_testSongs));
        await _service.SetShuffleAsync(true);

        await _service.SetShuffleAsync(false);

        _service.IsShuffleEnabled.Should().BeFalse();
        _service.PlayOrder.Should().Equal(Ids(_testSongs));
        _settingsService.Get(CoreSettings.IsShuffleEnabled).Should().BeFalse();
    }

    [Fact]
    public async Task PlayAsync_WithShuffleEnabledAndStartIndex_PlaysSelectedSongFirst()
    {
        await _service.InitializeAsync();
        await _service.SetShuffleAsync(true);
        var targetIndex = 2;
        var targetSong = _testSongs[targetIndex];

        await _service.PlayAsync(Ids(_testSongs), targetIndex, startShuffled: null);

        _service.IsShuffleEnabled.Should().BeTrue();
        _service.CurrentTrack.Should().Be(targetSong);

        _service.PlayOrder.First().Should().Be(targetSong.Id);

        await _audioPlayer.Received(1).LoadAsync(targetSong);
    }

    [Fact]
    public async Task AddRangeToQueueAsync_WithShuffleEnabled_AddsToBothOrders()
    {
        await _service.InitializeAsync();
        await _service.PlayAsync(Ids(_testSongs));
        await _service.SetShuffleAsync(true);
        var songToAdd = new Song { Id = Guid.NewGuid(), Title = "New Song" };

        await _service.AddRangeToQueueAsync([songToAdd.Id]);

        _service.PlayOrder.Should().Contain(songToAdd.Id);
        _service.PlayOrder.Count.Should().Be(_testSongs.Count + 1);
        await _service.SetShuffleAsync(false);
        _service.PlayOrder[^1].Should().Be(songToAdd.Id);
    }

    [Fact]
    public async Task PlayNextAsync_WhenCalled_InsertsSongAfterCurrentTrack()
    {
        await _service.InitializeAsync();
        await _service.PlayAsync(Ids(_testSongs), 1);
        var songToPlayNext = new Song { Id = Guid.NewGuid(), Title = "Next Song" };
        _library.Reader.GetSongByIdAsync(songToPlayNext.Id).Returns(songToPlayNext);

        await _service.PlayNextAsync([songToPlayNext.Id]);

        _service.PlayOrder.Should().HaveCount(_testSongs.Count + 1);
        _service.PlayOrder[2].Should().Be(songToPlayNext.Id);
    }

    [Fact]
    public async Task PlayNextAsync_WithSongAlreadyInQueue_MovesSongToNextPosition()
    {
        await _service.InitializeAsync();
        await _service.PlayAsync(Ids(_testSongs));
        var songToMove = _testSongs[3];

        await _service.PlayNextAsync([songToMove.Id]);

        _service.PlayOrder.Should().HaveCount(_testSongs.Count);
        _service.PlayOrder[1].Should().Be(songToMove.Id);
    }

    [Fact]
    public async Task PlayNextAsync_WithEmptyQueue_AddsSongAtBeginning()
    {
        await _service.InitializeAsync();
        var songToAdd = _testSongs[0];

        await _service.PlayNextAsync([songToAdd.Id]);

        _service.PlayOrder.Should().HaveCount(1);
        _service.PlayOrder[0].Should().Be(songToAdd.Id);
    }

    [Fact]
    public async Task RemoveFromQueueAsync_WhenRemovingCurrentTrack_CapturesPositionBeforePlayerStops()
    {
        await _service.InitializeAsync();
        _library.Listening.StartListenSessionAsync(Arg.Any<Guid>(), Arg.Any<PlaybackContext>()).Returns(2101L);
        await _service.PlayAsync(Ids(_testSongs), 1);

        var position = TimeSpan.FromSeconds(91);
        _audioPlayer.CurrentPosition.Returns(_ => position);
        _audioPlayer.StopAsync().Returns(_ =>
        {
            position = TimeSpan.Zero;
            return Task.CompletedTask;
        });
        _library.Listening.StartListenSessionAsync(Arg.Any<Guid>(), Arg.Any<PlaybackContext>()).Returns(2102L);

        await _service.RemoveFromQueueAsync(_testSongs[1].Id);
        await _service.FlushPendingFinalizationAsync();

        await _library.Listening.Received(1).FinalizeListenSessionAsync(
            2101L,
            TimeSpan.FromSeconds(91),
            PlaybackEndReason.Skipped);
        _service.CurrentListenHistoryId.Should().Be(2102L);
    }

    [Fact]
    public async Task RemoveFromQueueAsync_WhenCurrentTrackIsRemoved_KeepsTheSleepTimer()
    {
        await _service.InitializeAsync();
        await _service.PlayAsync(Ids(_testSongs), 1);
        _service.ScheduleStop(PlaybackStopMode.Timer, TimeSpan.FromMinutes(30));

        await _service.RemoveFromQueueAsync(_testSongs[1].Id);

        _service.CurrentTrack.Should().Be(_testSongs[2]);
        _service.StopMode.Should().Be(PlaybackStopMode.Timer);
    }

    [Fact]
    public async Task RemoveFromQueueAsync_WhenLastCurrentTrackIsRemovedWithoutRepeat_Stops()
    {
        await _service.InitializeAsync();
        await _service.PlayAsync(Ids(_testSongs), _testSongs.Count - 1);

        await _service.RemoveFromQueueAsync(_testSongs[^1].Id);

        _service.CurrentTrack.Should().BeNull();
        _service.PlayOrder.Should().NotBeEmpty();
        await _audioPlayer.Received(1).StopAsync();
    }

    [Fact]
    public async Task RemoveFromQueueAsync_WhenLastCurrentTrackIsRemovedWithRepeatAll_WrapsToFirstSurvivor()
    {
        await _service.InitializeAsync();
        await _service.SetRepeatModeAsync(RepeatMode.RepeatAll);
        await _service.PlayAsync(Ids(_testSongs), _testSongs.Count - 1);

        await _service.RemoveFromQueueAsync(_testSongs[^1].Id);

        _service.CurrentTrack.Should().Be(_testSongs[0]);
    }

    [Fact]
    public async Task RemoveFromQueueAsync_WhenRemovingTrackBeforeCurrent_AdjustsCurrentIndex()
    {
        await _service.InitializeAsync();

        await _service.PlayAsync(Ids(_testSongs), 2);
        var songToRemove = _testSongs[0];

        await _service.RemoveFromQueueAsync(songToRemove.Id);

        _service.PlayOrder.Should().NotContain(songToRemove.Id);
        _service.CurrentTrack.Should().Be(_testSongs[2]);
        _service.UpcomingQueue[0].Should().Be(_testSongs[2].Id);
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
        _playbackStateStore.SaveAsync(Arg.Do<PlaybackState>(state => savedState = state))
            .Returns(Task.CompletedTask);

        await _service.ClearQueueAsync();
        await _service.SavePlaybackStateAsync();

        _service.PlayOrder.Should().BeEmpty();
        _service.UpcomingQueue.Should().BeEmpty();
        _service.CurrentTrack.Should().BeNull();
        await _audioPlayer.Received(1).StopAsync();
        savedState.Should().NotBeNull();
        savedState!.PlaybackQueueTrackIds.Should().BeEmpty();
        savedState.ShuffledQueueTrackIds.Should().BeEmpty();
        savedState.CurrentTrackId.Should().BeNull();

        _settingsService.Set(CoreSettings.RestorePlaybackState, true);
        _playbackStateStore.LoadAsync().Returns(savedState);
        using var restored = new MusicPlaybackService(_settingsService, _playbackStateStore, _audioPlayer, _mediaControls, new ImmediateDispatcher(), _library.Reader, _smartPlaylists, _library.Listening, _metadataService, _fileSystem, _musicServers, _logger);
        await restored.InitializeAsync();
        restored.PlayOrder.Should().BeEmpty();
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
        (await _service.GetQueueSongsAsync([previousId])).Should().BeEmpty();
        _metadataService.ExtractMetadataAsync(path).Returns(new SongFileMetadata { Title = "After edit" });
        await _service.AddTransientFilesToQueueAsync([path]);

        _service.PlayOrder.Should().ContainSingle();
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
        _settingsService.Get(CoreSettings.RepeatMode).Should().Be(RepeatMode.RepeatOne);
        eventTracker.RepeatModeChangedCount.Should().Be(1);
    }

    [Fact]
    public async Task SavePlaybackStateAsync_WhenCalled_SavesCorrectState()
    {
        await _service.InitializeAsync();

        await _service.PlayAsync(Ids(_testSongs), 2);
        _audioPlayer.CurrentPosition.Returns(TimeSpan.FromSeconds(45));

        await _service.SavePlaybackStateAsync();

        await _playbackStateStore.Received(1).SaveAsync(Arg.Is<PlaybackState>(state =>
            state != null &&
            state.CurrentTrackId == _testSongs[2].Id &&
            state.PlaybackQueueTrackIds.SequenceEqual(_testSongs.Select(s => s.Id)) &&
            state.CurrentPlaybackQueueIndex == 2
        ));
    }

    [Fact]
    public async Task OnAudioPlayerErrorOccurred_ShouldStopPlayback()
    {
        await _service.InitializeAsync();
        await _service.PlayAsync(_testSongs[0].Id);

        _audioPlayer.ErrorOccurred += Raise.Event<Action>();

        await _service.FlushPendingFinalizationAsync();
        _service.CurrentTrack.Should().BeNull();
        await _audioPlayer.Received(1).StopAsync();
    }

    [Fact]
    public async Task MediaControlsPlayPressed_AfterStop_PlaysTheQueue()
    {
        await _service.InitializeAsync();
        await _service.PlayAsync(Ids(_testSongs));
        await _service.StopAsync();

        _mediaControls.PlayPressed += Raise.Event<Action>();

        await _service.FlushPendingFinalizationAsync();
        _service.CurrentTrack.Should().Be(_testSongs[0]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MediaControlsPlayAndPause_DoNotToggleWhenAlreadyInThatState(bool isPlaying)
    {
        await _service.InitializeAsync();
        await _service.PlayAsync(_testSongs[0].Id);
        _audioPlayer.IsPlaying.Returns(isPlaying);
        _audioPlayer.ClearReceivedCalls();

        if (isPlaying) _mediaControls.PlayPressed += Raise.Event<Action>();
        else _mediaControls.PausePressed += Raise.Event<Action>();

        await _service.FlushPendingFinalizationAsync();
        await _audioPlayer.DidNotReceive().PlayAsync();
        await _audioPlayer.DidNotReceive().PauseAsync();
    }

    [Fact]
    public async Task MediaControlsNextPressed_ShouldCallNextAsync()
    {
        await _service.InitializeAsync();
        await _service.PlayAsync(Ids(_testSongs));

        _mediaControls.NextPressed += Raise.Event<Action>();

        await _service.FlushPendingFinalizationAsync();
        _service.CurrentTrack.Should().Be(_testSongs[1]);
    }

    [Fact]
    public async Task MediaControlsPreviousPressed_ShouldCallPreviousAsync()
    {
        await _service.InitializeAsync();
        await _service.PlayAsync(Ids(_testSongs), 1);
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

        await _service.PlayAsync(Ids(_testSongs), startIndex);
        _audioPlayer.MediaOpened += Raise.Event<Action>();

        _mediaControls.ReceivedCalls().Last(call => call.GetMethodInfo().Name == nameof(ISystemMediaControls.SetNavigationEnabled))
            .GetArguments().Should().Equal(canGoNext, true);
    }

    [Fact]
    public async Task SeekAsync_ReportsTheRequestedPositionWithTheTaggedDurationBeforeThePlayerKnowsTheLength()
    {
        _testSongs[0].Duration = TimeSpan.FromMinutes(3);
        await _service.InitializeAsync();
        await _service.PlayAsync(_testSongs[0].Id);
        _audioPlayer.CurrentPosition.Returns(TimeSpan.FromSeconds(5));

        await _service.SeekAsync(TimeSpan.FromSeconds(90));

        _mediaControls.Received().SetTimeline(TimeSpan.FromSeconds(90), TimeSpan.FromMinutes(3));
    }

    [Fact]
    public async Task SeekAsync_FinishingAfterATrackChange_DoesNotReportItsPosition()
    {
        await _service.InitializeAsync();
        await _service.PlayAsync(Ids(_testSongs));
        var seek = new TaskCompletionSource();
        _audioPlayer.SeekAsync(Arg.Any<TimeSpan>()).Returns(seek.Task);

        var seekTask = _service.SeekAsync(TimeSpan.FromSeconds(90));
        await _service.NextAsync();
        seek.SetResult();
        await seekTask;

        _mediaControls.DidNotReceive().SetTimeline(TimeSpan.FromSeconds(90), Arg.Any<TimeSpan>());
    }

    [Theory]
    [InlineData(90, true)]
    [InlineData(-1, false)]
    [InlineData(181, false)]
    public async Task MediaControlsSeekRequested_SeeksOnlyWithinTheTrack(int seconds, bool seeks)
    {
        _testSongs[0].Duration = TimeSpan.FromMinutes(3);
        await _service.InitializeAsync();
        await _service.PlayAsync(_testSongs[0].Id);
        var position = TimeSpan.FromSeconds(seconds);

        _mediaControls.SeekRequested += Raise.Event<Action<TimeSpan>>(position);
        await _service.FlushPendingFinalizationAsync();

        await _audioPlayer.Received(seeks ? 1 : 0).SeekAsync(position);
    }

    [Fact]
    public async Task MediaControls_FollowThePlayerAndStayClearedAfterStop()
    {
        await _service.InitializeAsync();
        await _service.PlayAsync(_testSongs[0].Id);
        _audioPlayer.MediaOpened += Raise.Event<Action>();
        _audioPlayer.IsPlaying.Returns(true);
        _audioPlayer.StateChanged += Raise.Event<Action>();
        await _mediaControls.Received(1).SetNowPlayingAsync(_testSongs[0]);
        _mediaControls.Received(1).SetPlaybackStatus(SystemMediaPlaybackStatus.Playing);

        await _service.StopAsync();
        _mediaControls.Received(1).Clear();
        _mediaControls.ClearReceivedCalls();
        // The player can report its last state change after the stop.
        _audioPlayer.StateChanged += Raise.Event<Action>();

        _mediaControls.DidNotReceive().SetPlaybackStatus(Arg.Any<SystemMediaPlaybackStatus>());
        _mediaControls.DidNotReceive().SetTimeline(Arg.Any<TimeSpan>(), Arg.Any<TimeSpan>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PlayNextAsync_PreservesRequestedOrderAfterMovingEarlierTracks(bool shuffled)
    {
        await _service.InitializeAsync();
        await _service.PlayAsync(Ids(_testSongs), 2);
        await _service.SetShuffleAsync(shuffled);

        await _service.PlayNextAsync([_testSongs[0].Id]);
        await _service.PlayNextAsync([_testSongs[1].Id]);

        _service.UpcomingQueue.Take(3).Should().Equal(_testSongs[2].Id, _testSongs[1].Id, _testSongs[0].Id);
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
        _library.Listening.StartListenSessionAsync(first.Id, Arg.Any<PlaybackContext>()).Returns(101L);
        _library.Listening.StartListenSessionAsync(second.Id, Arg.Any<PlaybackContext>()).Returns(202L);
        if (delayAt == "lookup")
            _library.Reader.GetSongByIdAsync(first.Id).Returns(async _ => { await release.Task; return (Song?)first; });
        else if (delayAt == "session")
            _library.Listening.StartListenSessionAsync(first.Id, Arg.Any<PlaybackContext>())
                .Returns(async _ => { await release.Task; return (long?)101L; });
        else
            _audioPlayer.LoadAsync(first).Returns(release.Task);

        var oldRequest = _service.PlayAsync(first.Id);
        if (stop) await _service.StopAsync();
        else await _service.PlayAsync(second.Id);
        release.SetResult();
        await oldRequest.WaitAsync(TimeSpan.FromSeconds(2));
        await _service.FlushPendingFinalizationAsync();

        _service.CurrentTrack.Should().Be(stop ? null : second);
        _service.CurrentListenHistoryId.Should().Be(stop ? null : 202L);
        await _audioPlayer.Received(stop ? 0 : 1).PlayAsync();
        if (delayAt == "session")
            await _library.Listening.Received(1).FinalizeListenSessionAsync(101L, TimeSpan.Zero, PlaybackEndReason.PausedAndAbandoned);
    }

    [Fact]
    public async Task PlayAlbumAsync_WhenLookupFinishesAfterNewSelection_KeepsNewQueue()
    {
        var release = new TaskCompletionSource<List<Guid>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var albumId = Guid.NewGuid();
        _library.Reader.GetSongIdsAsync(new SongScope.Album(albumId), null, SongSortOrder.TrackNumberAsc).Returns(release.Task);
        var oldRequest = _service.PlayAlbumAsync(albumId);

        await _service.PlayAsync(_testSongs[1].Id);
        release.SetResult([_testSongs[0].Id]);
        await oldRequest;

        _service.CurrentTrack.Should().Be(_testSongs[1]);
        _service.PlayOrder.Should().Equal(_testSongs[1].Id);
    }

    [Fact]
    public async Task PlaybackEnded_WhileAnotherTrackIsLoading_DoesNotSkipTheRequestedTrack()
    {
        await _service.PlayAsync(Ids(_testSongs));
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
        await _service.PlayAsync(Ids(_testSongs));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _audioPlayer.StopAsync().Returns(release.Task);
        var oldRequest = removeCurrent
            ? _service.RemoveFromQueueAsync(_testSongs[0].Id)
            : _service.ClearQueueAsync();

        await _service.PlayAsync(_testSongs[3].Id);
        release.SetResult();
        await oldRequest;

        _service.CurrentTrack.Should().Be(_testSongs[3]);
        _service.PlayOrder.Should().Equal(_testSongs[3].Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PlaybackEnded_WhenFinalizationIsDelayed_DoesNotAdvanceAfterNewRequestOrDisposal(bool dispose)
    {
        _library.Listening.StartListenSessionAsync(_testSongs[0].Id, Arg.Any<PlaybackContext>()).Returns(101L);
        await _service.PlayAsync(Ids(_testSongs));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _library.Listening.FinalizeListenSessionAsync(101L, Arg.Any<TimeSpan>(), PlaybackEndReason.Finished)
            .Returns(release.Task);
        _audioPlayer.PlaybackEnded += Raise.Event<Action>();
        var disposal = dispose ? _service.DisposeAsync().AsTask() : Task.CompletedTask;
        if (!dispose) await _service.PlayQueueItemAsync(_testSongs[3].Id);
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
        _library.Listening.StartListenSessionAsync(_testSongs[0].Id, Arg.Any<PlaybackContext>()).Returns(release.Task);
        var playback = _service.PlayAsync(_testSongs[0].Id);
        var disposal = _service.DisposeAsync().AsTask();
        disposal.IsCompleted.Should().BeFalse();

        release.SetResult(42);
        await Task.WhenAll(playback, disposal);

        await _library.Listening.Received(1).FinalizeListenSessionAsync(42, TimeSpan.Zero, PlaybackEndReason.PausedAndAbandoned);
        await _audioPlayer.DidNotReceive().LoadAsync(Arg.Any<Song>());
    }

    [Fact]
    public async Task PlayTransientFileAsync_AfterNormalizedTrack_ResetsReplayGain()
    {
        _settingsService.Set(CoreSettings.VolumeNormalization, true);
        _testSongs[0].ReplayGainTrackGain = -6;
        await _service.PlayAsync(_testSongs[0].Id);
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
        await _service.PlayAsync(Ids(_testSongs), 3);
        await _service.StopAsync();

        await _service.SavePlaybackStateAsync();

        await _playbackStateStore.Received().SaveAsync(Arg.Is<PlaybackState>(state =>
            state.CurrentTrackId == null && state.CurrentPlaybackQueueIndex == 3));
    }

    [Fact]
    public async Task InitializeAsync_WhenDisposedDuringSessionFileRead_DoesNotTouchAudio()
    {
        var release = new TaskCompletionSource<PlaybackState?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _playbackStateStore.LoadAsync().Returns(release.Task);
        var initialization = _service.InitializeAsync();
        var disposal = _service.DisposeAsync().AsTask();
        release.SetResult(null);

        await Task.WhenAll(initialization, disposal);

        await _audioPlayer.DidNotReceive().SetVolumeAsync(Arg.Any<double>());
    }

    [Fact]
    public async Task PlayPauseAsync_AfterSessionRestore_StartsListenBeforePlaying()
    {
        _settingsService.Set(CoreSettings.RestorePlaybackState, true);
        _playbackStateStore.LoadAsync().Returns(new PlaybackState
        {
            PlaybackQueueTrackIds = [_testSongs[0].Id],
            CurrentTrackId = _testSongs[0].Id
        });
        _library.Listening.StartListenSessionAsync(_testSongs[0].Id, Arg.Any<PlaybackContext>()).Returns(52L);
        await _service.InitializeAsync();
        _service.CurrentListenHistoryId.Should().BeNull();

        await _service.PlayPauseAsync();

        _service.CurrentListenHistoryId.Should().Be(52L);
        Received.InOrder(() =>
        {
            _library.Listening.StartListenSessionAsync(_testSongs[0].Id, Arg.Any<PlaybackContext>());
            _audioPlayer.PlayAsync();
        });
    }

    [Fact]
    public async Task ReplayGain_WhenDisabledDuringDatabaseRead_DoesNotApplyLateGain()
    {
        var release = new TaskCompletionSource<Song?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _settingsService.Set(CoreSettings.VolumeNormalization, true);
        _library.Reader.GetSongByIdAsync(_testSongs[0].Id).Returns(Task.FromResult<Song?>(_testSongs[0]), release.Task);
        var playback = _service.PlayAsync(_testSongs[0].Id);

        _settingsService.Set(CoreSettings.VolumeNormalization, false);
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

        _service.PlayOrder.Should().BeEmpty();
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

        _service.PlayOrder.Should().ContainSingle();
        _service.CurrentTrack!.Title.Should().Be("First");
    }

    [Fact]
    public void AudioPlayerVolumeChange_ShowsAndSavesTheVolumeAndMuteThePlayerReports()
    {
        var shown = 0;
        _service.VolumeStateChanged += () => shown++;
        _audioPlayer.Volume.Returns(0.33);
        _audioPlayer.IsMuted.Returns(true);

        // As when the user changes it in the Windows volume mixer.
        _audioPlayer.VolumeChanged += Raise.Event<Action>();

        shown.Should().Be(1);
        _settingsService.Get(CoreSettings.Volume).Should().Be(0.33);
        _settingsService.Get(CoreSettings.IsMuted).Should().BeTrue();
    }

    [Fact]
    public async Task SavePlaybackStateAsync_WhenRestoringIsOff_ClearsTheSavedSession()
    {
        await _service.InitializeAsync();
        await _service.PlayAsync(Ids(_testSongs));
        _settingsService.Set(CoreSettings.RestorePlaybackState, false);

        await _service.SavePlaybackStateAsync();

        _playbackStateStore.Received(1).Clear();
        await _playbackStateStore.DidNotReceive().SaveAsync(Arg.Any<PlaybackState>());
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
    public async Task NextAsync_AfterPlayingAFileOnItsOwn_StopsPlayback()
    {
        const string filePath = "C:\\temp\\transient.mp3";
        _metadataService.ExtractMetadataAsync(filePath).Returns(new SongFileMetadata { Title = "Transient" });
        await _service.InitializeAsync();

        await _service.PlayTransientFileAsync(filePath);

        _service.PlayOrder.Should().ContainSingle();

        await _service.NextAsync();

        _service.CurrentTrack.Should().BeNull();
        await _audioPlayer.Received(1).StopAsync();
    }

}
