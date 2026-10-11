using Microsoft.Extensions.Logging;
using Nagi.Core.Models;
using System.Runtime.InteropServices;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;
using Nagi.Core.Helpers;

namespace Nagi.Core.Services.Implementations;

public partial class MusicPlaybackService : IMusicPlaybackService
{
    private readonly IAudioPlayer _audioPlayer;
    private readonly ILibraryService _libraryService;
    private readonly ILogger<MusicPlaybackService> _logger;
    private readonly IMetadataService _metadataService;
    private readonly ISettingsService _settingsService;
    private readonly object _pendingOperationsLock = new();

    private int _playbackRequestVersion;
    private int _queueVersion;
    private int _replayGainVersion;
    private bool _isDisposed;
    private bool _isInitialized;
    private Task? _initializationTask;
    private bool _isEligibilityMarked;
    private DateTime? _currentListenStartedUtc;
    private List<Guid> _playbackQueue = new();
    private List<Guid> _shuffledQueue = new();
    private readonly Dictionary<Guid, Song> _transientTracks = new();
    private readonly Dictionary<string, Guid> _transientTrackIdsByPath = new(StringComparer.OrdinalIgnoreCase);

    // Reverse index dictionaries for O(1) lookups in large queues (500k+ songs)
    private readonly Dictionary<Guid, int> _playbackQueueIndex = new();
    private readonly Dictionary<Guid, int> _shuffledQueueIndex = new();

    private PlaybackContext _currentContext = new(PlaybackContextType.Library, null);

    private readonly HashSet<Task> _pendingOperations = new();

    public MusicPlaybackService(
        ISettingsService settingsService,
        IAudioPlayer audioPlayer,
        ILibraryService libraryService,
        IMetadataService metadataService,
        ILogger<MusicPlaybackService> logger, TimeProvider? timeProvider = null)
    {
        _settingsService = settingsService;
        _audioPlayer = audioPlayer;
        _libraryService = libraryService;
        _metadataService = metadataService;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;

        _audioPlayer.PlaybackEnded += OnAudioPlayerPlaybackEnded;
        _audioPlayer.StateChanged += OnAudioPlayerStateChanged;
        _audioPlayer.VolumeChanged += OnAudioPlayerVolumeChanged;
        _audioPlayer.PositionChanged += OnAudioPlayerPositionChanged;
        _audioPlayer.ErrorOccurred += OnAudioPlayerErrorOccurred;
        _audioPlayer.MediaOpened += OnAudioPlayerMediaOpened;
        _audioPlayer.DurationChanged += OnAudioPlayerDurationChanged;
        _audioPlayer.SmtcNextButtonPressed += OnAudioPlayerSmtcNextButtonPressed;
        _audioPlayer.SmtcPreviousButtonPressed += OnAudioPlayerSmtcPreviousButtonPressed;

        _settingsService.VolumeNormalizationEnabledChanged += OnVolumeNormalizationEnabledChanged;
        _settingsService.FadeOnPlayPauseEnabledChanged += OnFadeOnPlayPauseEnabledChanged;
        _settingsService.FadeInDurationChanged += OnFadeInDurationChanged;
        _settingsService.FadeOutDurationChanged += OnFadeOutDurationChanged;

        EqualizerBands = _audioPlayer.GetEqualizerBands();
    }

    public Song? CurrentTrack { get; private set; }
    public long? CurrentListenHistoryId { get; private set; }
    public bool IsPlaying => _audioPlayer.IsPlaying;
    public TimeSpan CurrentPosition => _audioPlayer.CurrentPosition;
    public TimeSpan Duration => _audioPlayer.Duration > TimeSpan.Zero
        ? _audioPlayer.Duration
        : (CurrentTrack?.Duration ?? TimeSpan.Zero);
    public double Volume => _audioPlayer.Volume;
    public bool IsMuted => _audioPlayer.IsMuted;
    public IReadOnlyList<Guid> PlaybackQueue => _playbackQueue.AsReadOnly();
    public IReadOnlyList<Guid> ShuffledQueue => _shuffledQueue.AsReadOnly();
    public int CurrentQueueIndex { get; private set; } = -1;
    public int CurrentShuffledIndex { get; private set; } = -1;
    public bool IsShuffleEnabled { get; private set; }
    public RepeatMode CurrentRepeatMode { get; private set; } = RepeatMode.Off;
    private bool _isTransitioningTrack;
    public IReadOnlyList<(uint Index, float Frequency)> EqualizerBands { get; }
    public IReadOnlyList<EqualizerPreset> AvailablePresets { get; } = new List<EqualizerPreset>
    {
        new(Resources.Strings.EqPreset_None, [0, 0, 0, 0, 0, 0, 0, 0, 0, 0]),
        new(Resources.Strings.EqPreset_Classical, [0, 0, 0, 0, 0, 0, -3.2f, -3.2f, -3.2f, -4.0f]),
        new(Resources.Strings.EqPreset_Dance, [3.2f, 2.4f, 0.8f, 0, 0, -1.6f, -2.4f, -2.4f, 0, 0]),
        new(Resources.Strings.EqPreset_FullBass, [3.2f, 3.2f, 3.2f, 1.6f, 0, -1.6f, -3.2f, -3.2f, -3.2f, -4.0f]),
        new(Resources.Strings.EqPreset_FullTreble, [-3.2f, -3.2f, -3.2f, -1.6f, 0, 1.6f, 3.2f, 3.2f, 3.2f, 4.0f]),
        new(Resources.Strings.EqPreset_Jazz, [1.6f, 0.8f, 0, 0.8f, -0.8f, -0.8f, -1.6f, 0, 0.8f, 1.6f]),
        new(Resources.Strings.EqPreset_Pop, [-1.6f, 1.6f, 2.4f, 2.4f, 1.6f, 0, -0.8f, -0.8f, -1.6f, -1.6f]),
        new(Resources.Strings.EqPreset_Rock, [3.2f, 2.4f, -1.6f, -2.4f, -0.8f, 1.6f, 2.4f, 3.2f, 3.2f, 3.2f]),
        new(Resources.Strings.EqPreset_Soft, [1.6f, 0.8f, 0, -0.8f, -0.8f, 0, 0.8f, 1.6f, 2.4f, 3.2f]),
        new(Resources.Strings.EqPreset_Techno, [3.2f, 2.4f, 0, -1.6f, -1.6f, 0, 1.6f, 2.4f, 2.4f, 3.2f]),
        new(Resources.Strings.EqPreset_Vocal, [-0.8f, -2.4f, -2.4f, 0, 1.6f, 1.6f, 1.6f, 0.8f, 0, -0.8f])
    };

    public EqualizerSettings? CurrentEqualizerSettings { get; private set; }

    public event Action? TrackChanged;
    public event Action? PlaybackStateChanged;
    public event Action? VolumeStateChanged;
    public event Action? ShuffleModeChanged;
    public event Action? RepeatModeChanged;
    public event Action? QueueChanged;
    public event Action? PositionChanged;
    public event Action? DurationChanged;
    public event Action? EqualizerChanged;
    public event Action<Song, long, DateTime>? ScrobbleEligibilityReached;

    public Task InitializeAsync(bool restoreLastSession = true)
    {
        if (_isDisposed) return Task.CompletedTask;
        if (_initializationTask != null) return _initializationTask;
        _initializationTask = InitializeCoreAsync(restoreLastSession);
        TrackPendingOperation(_initializationTask);
        return _initializationTask;
    }

    private async Task InitializeCoreAsync(bool restoreLastSession)
    {
        _logger.LogDebug("Initializing MusicPlaybackService...");

        try
        {
            var volumeTask = _settingsService.GetInitialVolumeAsync();
            var muteTask = _settingsService.GetInitialMuteStateAsync();
            var shuffleTask = _settingsService.GetInitialShuffleStateAsync();
            var repeatTask = _settingsService.GetInitialRepeatModeAsync();
            var eqTask = _settingsService.GetEqualizerSettingsAsync();
            var restoreEnabledTask = _settingsService.GetRestorePlaybackStateEnabledAsync();
            var fadeEnabledTask = _settingsService.GetFadeOnPlayPauseEnabledAsync();
            var fadeInTask = _settingsService.GetFadeInDurationMsAsync();
            var fadeOutTask = _settingsService.GetFadeOutDurationMsAsync();
            var playbackStateTask = _settingsService.GetPlaybackStateAsync();

            await Task.WhenAll(volumeTask, muteTask, shuffleTask, repeatTask, eqTask, restoreEnabledTask, fadeEnabledTask, fadeInTask, fadeOutTask, playbackStateTask);
            if (_isDisposed) return;

            await _audioPlayer.SetVolumeAsync(volumeTask.Result);
            if (_isDisposed) return;
            await _audioPlayer.SetMuteAsync(muteTask.Result);
            if (_isDisposed) return;
            _audioPlayer.SetFadeOnPlayPauseEnabled(fadeEnabledTask.Result);
            _audioPlayer.SetFadeInDuration(fadeInTask.Result);
            _audioPlayer.SetFadeOutDuration(fadeOutTask.Result);
            IsShuffleEnabled = shuffleTask.Result;
            CurrentRepeatMode = repeatTask.Result;

            CurrentEqualizerSettings = eqTask.Result;
            if (CurrentEqualizerSettings != null && CurrentEqualizerSettings.BandGains.Count != EqualizerBands.Count)
            {
                _logger.LogWarning("Equalizer settings band count ({SavedCount}) does not match available bands ({AvailableCount}). Resetting equalizer to default.",
                    CurrentEqualizerSettings.BandGains.Count, EqualizerBands.Count);

                CurrentEqualizerSettings = null; // Force recreation below
            }

            if (CurrentEqualizerSettings == null)
            {
                CurrentEqualizerSettings = new EqualizerSettings
                {
                    Preamp = EqualizerSettings.DefaultPreampDb,
                    BandGains = Enumerable.Repeat(0.0f, EqualizerBands.Count).ToList()
                };

                await _settingsService.SetEqualizerSettingsAsync(CurrentEqualizerSettings);
                if (_isDisposed) return;
            }
            _audioPlayer.ApplyEqualizerSettings(CurrentEqualizerSettings);

            var restoredSuccessfully = false;
            if (restoreLastSession && restoreEnabledTask.Result)
            {
                var savedState = playbackStateTask.Result;
                if (savedState != null) restoredSuccessfully = await RestoreInternalPlaybackStateAsync(savedState);
            }

            if (_isDisposed) return;
            if (!restoredSuccessfully) ClearQueuesInternal();

            _logger.LogDebug("MusicPlaybackService initialized successfully. Session restored: {IsRestored}",
                restoredSuccessfully);
        }
        catch (Exception ex)
        {
            if (_isDisposed) return;
            _logger.LogError(ex, "MusicPlaybackService initialization failed. Using default settings.");
            await _audioPlayer.SetVolumeAsync(0.5);
            if (_isDisposed) return;
            await _audioPlayer.SetMuteAsync(false);
            if (_isDisposed) return;
            IsShuffleEnabled = false;
            CurrentRepeatMode = RepeatMode.Off;
            ClearQueuesInternal();
        }

        if (_isDisposed) return;
        _isInitialized = true;
        UpdateSmtcControls();

        VolumeStateChanged?.Invoke();
        ShuffleModeChanged?.Invoke();
        RepeatModeChanged?.Invoke();
        QueueChanged?.Invoke();
        TrackChanged?.Invoke();
        PlaybackStateChanged?.Invoke();
        PositionChanged?.Invoke();
    }

    private void CommitQueueChanges()
    {
        _logger.LogTrace("Committing queue changes and rebuilding indices.");
        RebuildPlaybackQueueIndex();

        if (IsShuffleEnabled)
        {
            var retained = _playbackQueue.ToHashSet();
            _shuffledQueue.RemoveAll(id => !retained.Contains(id));
            retained.ExceptWith(_shuffledQueue);
            var additions = retained.ToArray();
            Random.Shared.Shuffle(additions);
            _shuffledQueue.AddRange(additions);
            RebuildShuffledQueueIndex();
        }

        if (CurrentTrack != null)
        {
            CurrentQueueIndex = GetPlaybackQueueIndex(CurrentTrack.Id);
            CurrentShuffledIndex = IsShuffleEnabled ? GetShuffledQueueIndex(CurrentTrack.Id) : -1;
        }
        else
        {
            CurrentQueueIndex = Math.Clamp(CurrentQueueIndex, -1, _playbackQueue.Count - 1);
            CurrentShuffledIndex = IsShuffleEnabled
                ? Math.Clamp(CurrentShuffledIndex, -1, _shuffledQueue.Count - 1)
                : -1;
        }

        PruneTransientTracks();
        QueueChanged?.Invoke();
        UpdateSmtcControls();

    }

    public Task PlayTransientFileAsync(string filePath) =>
        RunPlaybackRequest(request => PlayTransientFileInternalAsync(filePath, request));

    private async Task PlayTransientFileInternalAsync(string filePath, int request)
    {
        _queueVersion++;
        _logger.LogDebug("Playing transient file: {FilePath}", filePath);
        var transientSong = await CreateTransientSongAsync(filePath);
        if (!IsCurrentRequest(request)) return;

        if (CurrentListenHistoryId.HasValue)
        {
            var sessionId = CurrentListenHistoryId.Value;
            var position = _audioPlayer.CurrentPosition;
            CurrentListenHistoryId = null;
            FireAndForgetSafe(() => _libraryService.FinalizeListenSessionAsync(sessionId, position, PlaybackEndReason.Skipped), "Finalizing session on Transient Playback", trackForDisposal: true);
        }

        _isEligibilityMarked = false;

        _isTransitioningTrack = true;
        ClearQueuesInternal();
        RegisterTransientTrack(transientSong);
        QueueChanged?.Invoke();

        CurrentTrack = transientSong;
        CurrentQueueIndex = -1;
        CurrentListenHistoryId = null;
        _currentContext = new PlaybackContext(PlaybackContextType.Transient, null);

        // Track listens for transient playback only when the file maps to a known library song.
        // Truly external files remain untracked because ListenHistory requires a Song FK.
        var existingLibrarySong = await _libraryService.GetSongByFilePathAsync(filePath);
        if (!IsCurrentRequest(request)) return;
        if (existingLibrarySong != null)
            await StartListenSessionAsync(existingLibrarySong.Id, request);
        if (!IsCurrentRequest(request)) return;

        await _audioPlayer.LoadAsync(transientSong);
        if (!IsCurrentRequest(request)) return;
        await ApplyReplayGainIfEnabledAsync();
        if (!IsCurrentRequest(request)) return;
        await _audioPlayer.PlayAsync();
        if (!IsCurrentRequest(request)) return;
        UpdateSmtcControls();

        TrackChanged?.Invoke();
    }

    private async Task<Song> CreateTransientSongAsync(string filePath)
    {
        var metadata = await _metadataService.ExtractMetadataAsync(filePath);

        var validArtists = metadata.Artists
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Select(a => ArtistNameHelper.Normalize(a))
            .ToList();
        if (validArtists.Count == 0)
        {
            validArtists.Add(Artist.UnknownArtistName);
        }

        var validAlbumArtists = metadata.AlbumArtists
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Select(a => ArtistNameHelper.Normalize(a))
            .ToList();
        if (validAlbumArtists.Count == 0)
        {
            validAlbumArtists = validArtists;
        }

        return new Song
        {
            FilePath = filePath,
            Title = metadata.Title,
            ArtistName = Artist.GetDisplayName(validArtists),
            PrimaryArtistName = validArtists.First(),

            SongArtists = validArtists.Select((a, i) => new SongArtist
            {
                Artist = new Artist { Name = a },
                Order = i
            }).ToList(),
            Album = new Album
            {
                Title = ArtistNameHelper.NormalizeStringCore(metadata.Album) ?? Album.UnknownAlbumName,

                ArtistName = Artist.GetDisplayName(validAlbumArtists),
                PrimaryArtistName = validAlbumArtists.First(),

                AlbumArtists = validAlbumArtists.Select((aa, i) => new AlbumArtist
                {
                    Artist = new Artist { Name = aa },
                    Order = i
                }).ToList()
            },
            Duration = metadata.Duration,
            Genres = metadata.Genres.Where(g => !string.IsNullOrWhiteSpace(g))
                .Distinct(StringComparer.OrdinalIgnoreCase).Select(g => new Genre { Name = g }).ToList(),
            AlbumArtUriFromTrack = metadata.CoverArtUri,
            Lyrics = metadata.Lyrics,
            LrcFilePath = metadata.LrcFilePath
        };
    }

    public Task AddTransientFilesToQueueAsync(IEnumerable<string> filePaths)
    {
        if (_isDisposed) return Task.CompletedTask;
        var task = AddTransientFilesInternalAsync(filePaths);
        TrackPendingOperation(task);
        return task;
    }

    private async Task AddTransientFilesInternalAsync(IEnumerable<string> filePaths)
    {
        var queueVersion = _queueVersion;
        var request = _playbackRequestVersion;
        var paths = filePaths?
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? [];
        if (paths.Count == 0) return;

        var songs = new List<(Song Song, bool Transient)>(paths.Count);
        foreach (var path in paths)
        {
            if (_transientTrackIdsByPath.TryGetValue(path, out var transientId))
            {
                songs.Add((_transientTracks[transientId], true));
                continue;
            }

            var librarySong = await _libraryService.GetSongByFilePathAsync(path);
            if (_isDisposed || queueVersion != _queueVersion) return;
            if (librarySong != null)
            {
                songs.Add((librarySong, false));
                continue;
            }

            var transientSong = await CreateTransientSongAsync(path);
            if (_isDisposed || queueVersion != _queueVersion) return;
            songs.Add((transientSong, true));
        }

        if (_playbackQueue.Count == 0 && CurrentTrack != null) _playbackQueue.Add(CurrentTrack.Id);
        var queuedIds = _playbackQueue.ToHashSet();
        foreach (var item in songs)
        {
            var song = item.Song;
            if (item.Transient)
            {
                // Another add may have resolved the same path while metadata was loading.
                if (_transientTrackIdsByPath.TryGetValue(song.FilePath, out var existingId))
                    song = _transientTracks[existingId];
                else
                    RegisterTransientTrack(song);
            }
            if (queuedIds.Add(song.Id)) _playbackQueue.Add(song.Id);
        }
        CommitQueueChanges();

        if (CurrentTrack == null && _playbackQueue.Count > 0 && IsCurrentRequest(request))
        {
            _currentContext = new PlaybackContext(PlaybackContextType.Transient, null);
            await PlayQueueItemAsync(0);
        }
    }

    public async Task<IReadOnlyDictionary<Guid, Song>> GetQueueTracksAsync(IEnumerable<Guid> songIds)
    {
        var ids = songIds?.Distinct().ToList() ?? [];
        var result = new Dictionary<Guid, Song>(ids.Count);
        var libraryIds = new List<Guid>(ids.Count);

        foreach (var id in ids)
        {
            if (_transientTracks.TryGetValue(id, out var transientTrack))
                result[id] = transientTrack;
            else
                libraryIds.Add(id);
        }

        if (libraryIds.Count > 0)
        {
            var libraryTracks = await _libraryService.GetSongsByIdsAsync(libraryIds);
            foreach (var pair in libraryTracks) result[pair.Key] = pair.Value;
        }

        return result;
    }

    public async Task<IReadOnlyDictionary<Guid, Song>> GetQueueSongsAsync(IEnumerable<Guid> songIds,
        CancellationToken cancellationToken = default)
    {
        var ids = songIds.Distinct().ToArray();
        var transient = ids.Where(_transientTracks.ContainsKey).ToDictionary(id => id, id => _transientTracks[id]);
        var songs = await Task.Run(() => _libraryService.GetQueueSongsAsync(
            ids.Where(id => !transient.ContainsKey(id)), cancellationToken), cancellationToken);
        foreach (var pair in songs) transient[pair.Key] = pair.Value;
        return transient;
    }

    public async Task<TimeSpan> GetQueueDurationAsync(IEnumerable<Guid> songIds,
        CancellationToken cancellationToken = default)
    {
        var libraryIds = new List<Guid>();
        long transientTicks = 0;
        foreach (var id in songIds.Distinct())
        {
            if (_transientTracks.TryGetValue(id, out var song)) transientTicks += song.DurationTicks;
            else libraryIds.Add(id);
        }
        var duration = await Task.Run(() => _libraryService.GetSongsDurationAsync(libraryIds, cancellationToken),
            cancellationToken);
        return duration + TimeSpan.FromTicks(transientTicks);
    }

    public void MoveQueueItem(Guid songId, Guid? beforeSongId)
    {
        var queue = IsShuffleEnabled ? _shuffledQueue : _playbackQueue;
        var from = queue.IndexOf(songId);
        var target = beforeSongId.HasValue ? queue.IndexOf(beforeSongId.Value) : queue.Count;
        if (from < 0 || target < 0 || from == target || from + 1 == target) return;

        queue.RemoveAt(from);
        queue.Insert(target > from ? target - 1 : target, songId);
        CommitQueueChanges();
    }

    private void RegisterTransientTrack(Song song)
    {
        _transientTracks[song.Id] = song;
        _transientTrackIdsByPath[song.FilePath] = song.Id;
    }

    private void PruneTransientTracks()
    {
        var retainedIds = _playbackQueue.ToHashSet();
        if (CurrentTrack != null) retainedIds.Add(CurrentTrack.Id);

        foreach (var id in _transientTracks.Keys.Where(id => !retainedIds.Contains(id)).ToList())
        {
            _transientTrackIdsByPath.Remove(_transientTracks[id].FilePath);
            _transientTracks.Remove(id);
        }
    }

    public Task PlayAsync(Song song)
    {
        return PlayAsync(song?.Id ?? Guid.Empty);
    }

    public Task PlayAsync(Guid songId) => songId == Guid.Empty
        ? Task.CompletedTask
        : PlayAsync([songId]);

    public Task PlayAsync(IEnumerable<Song> songs, int startIndex = 0, bool? startShuffled = null)
    {
        return PlayAsync(songs?.Select(s => s.Id) ?? Enumerable.Empty<Guid>(), startIndex, startShuffled);
    }

    public Task PlayAsync(IEnumerable<Guid> songIds, int startIndex = 0, bool? startShuffled = null, PlaybackContext? context = null) =>
        RunPlaybackRequest(request => PlayInternalAsync(songIds, startIndex, startShuffled, context, request));

    private async Task PlayInternalAsync(IEnumerable<Guid> songIds, int startIndex, bool? startShuffled, PlaybackContext? context, int request, IReadOnlyList<Guid>? shuffleOrder = null, ShuffleMode mode = ShuffleMode.Random)
    {
        _queueVersion++;
        var requestedIds = songIds?.ToList() ?? [];
        var requestedStartId = startIndex >= 0 && startIndex < requestedIds.Count ? requestedIds[startIndex] : Guid.Empty;
        var idList = requestedIds.Where(id => id != Guid.Empty).Distinct().ToList();
        startIndex = requestedStartId == Guid.Empty ? 0 : idList.IndexOf(requestedStartId);
        if (!idList.Any())
        {
            _logger.LogWarning("PlayAsync called with an empty song list. Stopping playback.");
            await StopInternalAsync(request);
            if (!IsCurrentRequest(request)) return;
            ClearQueuesInternal();
            QueueChanged?.Invoke();
            UpdateSmtcControls();
            return;
        }

        _logger.LogDebug(
            "Playing a new queue of {SongCount} songs. Start index: {StartIndex}, Shuffled: {IsShuffled}",
            idList.Count, startIndex, startShuffled);

        _currentContext = context ?? new PlaybackContext(PlaybackContextType.Library, null);
        if (startShuffled.HasValue && IsShuffleEnabled != startShuffled.Value) await SetShuffleAsync(startShuffled.Value);

        if (!IsCurrentRequest(request)) return;

        _playbackQueue = idList;
        if (IsShuffleEnabled)
        {
            if (shuffleOrder is null) GenerateShuffledQueue();
            else _shuffledQueue = [.. shuffleOrder];
        }
        CurrentShuffleMode = mode;
        ShuffleModeChanged?.Invoke();
        CommitQueueChanges();

        if (IsShuffleEnabled)
        {
            // If playing from a specific item (not just "Shuffle All"), ensure it plays first
            if (startShuffled != true && startIndex >= 0 && startIndex < _playbackQueue.Count)
            {
                var targetSongId = _playbackQueue[startIndex];
                _shuffledQueue.Remove(targetSongId);
                _shuffledQueue.Insert(0, targetSongId);
                RebuildShuffledQueueIndex();
            }

            startIndex = GetPlaybackQueueIndex(_shuffledQueue[0]);
        }

        if (IsCurrentRequest(request)) await PlayQueueItemInternalAsync(startIndex, request);
    }

    public Task PlayPauseAsync() =>
        RunPlaybackRequest(PlayPauseInternalAsync);

    private async Task PlayPauseInternalAsync(int request)
    {
        if (_isTransitioningTrack)
        {
            await StopInternalAsync(request);
            return;
        }

        if (_audioPlayer.IsPlaying)
        {
            await _audioPlayer.PauseAsync();
            return;
        }

        if (CurrentTrack != null)
        {
            if (!CurrentListenHistoryId.HasValue && !_transientTracks.ContainsKey(CurrentTrack.Id))
                await StartListenSessionAsync(CurrentTrack.Id, request);
            if (!IsCurrentRequest(request)) return;
            await ApplyReplayGainIfEnabledAsync();
            if (!IsCurrentRequest(request)) return;
            await _audioPlayer.PlayAsync();
            return;
        }

        // If no track is loaded but a queue exists, play from the last known position.
        if (_playbackQueue.Any())
        {
            var indexToPlay = CurrentQueueIndex >= 0 ? CurrentQueueIndex : 0;

            if (IsShuffleEnabled && _shuffledQueue.Any())
            {
                var shuffledIndex = CurrentShuffledIndex >= 0 ? CurrentShuffledIndex : 0;
                var songIdToPlay = _shuffledQueue.ElementAtOrDefault(shuffledIndex);
                if (songIdToPlay != Guid.Empty)
                {
                    indexToPlay = GetPlaybackQueueIndex(songIdToPlay);
                    if (indexToPlay == -1) indexToPlay = 0;
                }
            }

            if (indexToPlay >= 0 && indexToPlay < _playbackQueue.Count) await PlayQueueItemInternalAsync(indexToPlay, request);
        }
    }

    public Task StopAsync() =>
        RunPlaybackRequest(StopInternalAsync);

    private async Task StopInternalAsync(int request)
    {
        if (CurrentListenHistoryId.HasValue)
        {
            // Capture and clear synchronously before the async gap, mirroring the pattern in
            // OnAudioPlayerPlaybackEnded, to prevent a racing PlaybackEnded event from issuing
            // a second FinalizeListenSessionAsync on the same session.
            var sessionId = CurrentListenHistoryId.Value;
            var position = _audioPlayer.CurrentPosition;
            CurrentListenHistoryId = null;
            FireAndForgetSafe(() => _libraryService.FinalizeListenSessionAsync(sessionId, position, PlaybackEndReason.PausedAndAbandoned), "Finalizing session on Stop", trackForDisposal: true);
        }

        await _audioPlayer.StopAsync();
        if (!IsCurrentRequest(request)) return;

        // Null the track to indicate a "stopped" state but preserve the queue indices.
        // This is crucial for Next/Previous commands to work correctly from a stopped state.
        CurrentTrack = null;
        CurrentListenHistoryId = null;
        _currentListenStartedUtc = null;
        _isTransitioningTrack = false;
        TrackChanged?.Invoke();
        PositionChanged?.Invoke();
        UpdateSmtcControls();
    }

    public Task NextAsync() =>
        RunPlaybackRequest(NextInternalAsync);

    private async Task NextInternalAsync(int request)
    {
        if (CurrentRepeatMode == RepeatMode.RepeatOne && CurrentTrack != null)
        {
            await PlayQueueItemInternalAsync(CurrentQueueIndex, request);
            return;
        }

        if (CurrentTrack != null)
        {
            var nextIndex = GetAdjacentTrackIndex(CurrentQueueIndex, moveForward: true);
            if (nextIndex >= 0)
                await PlayQueueItemInternalAsync(nextIndex, request);
            else
                await StopInternalAsync(request);
        }
        else if (_playbackQueue.Any())
        {
            // Pressing Next should resume playback from the current position.
            await PlayQueueItemInternalAsync(CurrentQueueIndex, request);
        }
    }

    public Task PreviousAsync() =>
        RunPlaybackRequest(PreviousInternalAsync);

    private async Task PreviousInternalAsync(int request)
    {
        // If the track has played for more than 3 seconds, restart it.
        if (CurrentTrack != null && _audioPlayer.CurrentPosition.TotalSeconds > 3 &&
            CurrentRepeatMode != RepeatMode.RepeatOne)
        {
            await SeekAsync(TimeSpan.Zero);
            if (!IsCurrentRequest(request)) return;
            if (!_audioPlayer.IsPlaying) await _audioPlayer.PlayAsync();
            return;
        }

        if (CurrentRepeatMode == RepeatMode.RepeatOne && CurrentTrack != null)
        {
            await PlayQueueItemInternalAsync(CurrentQueueIndex, request);
            return;
        }

        if (CurrentTrack != null)
        {
            var prevIndex = GetAdjacentTrackIndex(CurrentQueueIndex, moveForward: false);
            if (prevIndex >= 0)
                await PlayQueueItemInternalAsync(prevIndex, request);
            else
                await StopInternalAsync(request);
        }
        else if (_playbackQueue.Any())
        {
            // Pressing Previous should resume playback from the current position.
            await PlayQueueItemInternalAsync(CurrentQueueIndex, request);
        }
    }

    public async Task SeekAsync(TimeSpan position)
    {
        if (CurrentTrack != null) await _audioPlayer.SeekAsync(position);
    }

    public Task PlayAlbumAsync(Guid albumId) => RunPlaybackRequest(async request =>
    {
        var songIds = await _libraryService.GetAllSongIdsByAlbumIdAsync(albumId, SongSortOrder.TrackNumberAsc);
        if (IsCurrentRequest(request))
            await PlayInternalAsync(songIds, 0, null, new PlaybackContext(PlaybackContextType.Album, albumId), request);
    });

    public Task PlayArtistAsync(Guid artistId) => RunPlaybackRequest(async request =>
    {
        var songIds = await _libraryService.GetAllSongIdsByArtistIdAsync(artistId, SongSortOrder.TitleAsc);
        if (IsCurrentRequest(request))
            await PlayInternalAsync(songIds, 0, null, new PlaybackContext(PlaybackContextType.Artist, artistId), request);
    });

    public Task PlayFolderAsync(Guid folderId) => RunPlaybackRequest(async request =>
    {
        var songIds = await _libraryService.GetAllSongIdsByFolderIdAsync(folderId, SongSortOrder.TitleAsc);
        if (IsCurrentRequest(request))
            await PlayInternalAsync(songIds, 0, null, new PlaybackContext(PlaybackContextType.Folder, folderId), request);
    });

    public Task PlayPlaylistAsync(Guid playlistId) => RunPlaybackRequest(async request =>
    {
        var songIds = await _libraryService.GetAllSongIdsByPlaylistIdAsync(playlistId, SongSortOrder.PlaylistOrder);
        if (IsCurrentRequest(request))
            await PlayInternalAsync(songIds, 0, null, new PlaybackContext(PlaybackContextType.Playlist, playlistId), request);
    });

    public Task PlayGenreAsync(Guid genreId) => RunPlaybackRequest(async request =>
    {
        var songIds = await _libraryService.GetAllSongIdsByGenreIdAsync(genreId, SongSortOrder.TitleAsc);
        if (IsCurrentRequest(request))
            await PlayInternalAsync(songIds, 0, null, new PlaybackContext(PlaybackContextType.Genre, genreId), request);
    });

    public async Task SetVolumeAsync(double volume)
    {
        await _audioPlayer.SetVolumeAsync(volume);
        await _settingsService.SaveVolumeAsync(_audioPlayer.Volume);
    }

    public async Task ToggleMuteAsync()
    {
        var newMuteState = !_audioPlayer.IsMuted;
        await _audioPlayer.SetMuteAsync(newMuteState);
        await _settingsService.SaveMuteStateAsync(newMuteState);
    }

    public async Task SetShuffleAsync(bool enable)
    {
        if (IsShuffleEnabled == enable) return;

        IsShuffleEnabled = enable;
        CurrentShuffleMode = ShuffleMode.Random;
        _logger.LogDebug("Shuffle mode set to {ShuffleState}", IsShuffleEnabled);

        if (IsShuffleEnabled)
        {
            GenerateShuffledQueue();
        }
        else
        {
            _shuffledQueue.Clear();
        }
        CommitQueueChanges();

        await _settingsService.SaveShuffleStateAsync(IsShuffleEnabled);
        ShuffleModeChanged?.Invoke();
        UpdateSmtcControls();
    }

    public async Task SetRepeatModeAsync(RepeatMode mode)
    {
        if (CurrentRepeatMode == mode) return;

        CurrentRepeatMode = mode;
        _logger.LogDebug("Repeat mode set to {RepeatMode}", CurrentRepeatMode);
        await _settingsService.SaveRepeatModeAsync(CurrentRepeatMode);
        RepeatModeChanged?.Invoke();
        UpdateSmtcControls();
    }

    public Task AddToQueueAsync(Song song)
    {
        return AddToQueueAsync(song?.Id ?? Guid.Empty);
    }

    public Task AddToQueueAsync(Guid songId)
    {
        if (songId == Guid.Empty || GetPlaybackQueueIndex(songId) >= 0) return Task.CompletedTask;

        _playbackQueue.Add(songId);
        CommitQueueChanges();

        return Task.CompletedTask;
    }

    public Task AddRangeToQueueAsync(IEnumerable<Song> songs)
    {
        return AddRangeToQueueAsync(songs?.Select(s => s.Id) ?? Enumerable.Empty<Guid>());
    }

    public Task AddRangeToQueueAsync(IEnumerable<Guid> songIds)
    {
        if (songIds == null) return Task.CompletedTask;

        var queuedIds = _playbackQueue.ToHashSet();
        var idsToAdd = songIds
            .Where(id => id != Guid.Empty && queuedIds.Add(id))
            .ToList();

        if (idsToAdd.Count > 0)
        {
            _playbackQueue.AddRange(idsToAdd);
            CommitQueueChanges();
        }

        return Task.CompletedTask;
    }

    public Task PlayNextAsync(Song song)
    {
        return PlayNextAsync(song?.Id ?? Guid.Empty);
    }

    public Task PlayNextAsync(Guid songId)
    {
        if (songId == Guid.Empty) return Task.CompletedTask;

        MoveAfterCurrent(_playbackQueue, songId, CurrentQueueIndex);
        if (IsShuffleEnabled) MoveAfterCurrent(_shuffledQueue, songId, CurrentShuffledIndex);
        CommitQueueChanges();

        return Task.CompletedTask;
    }

    private void MoveAfterCurrent(List<Guid> queue, Guid songId, int stoppedIndex)
    {
        var currentIndex = CurrentTrack == null ? stoppedIndex : queue.IndexOf(CurrentTrack.Id);
        var removedIndex = queue.IndexOf(songId);
        if (removedIndex >= 0)
        {
            queue.RemoveAt(removedIndex);
            if (removedIndex < currentIndex) currentIndex--;
        }

        queue.Insert(Math.Clamp(currentIndex + 1, 0, queue.Count), songId);
    }

    public Task RemoveFromQueueAsync(Song song)
    {
        return RemoveFromQueueAsync(song?.Id ?? Guid.Empty);
    }

    public Task RemoveFromQueueAsync(Guid songId) =>
        GetPlaybackQueueIndex(songId) < 0 ? Task.CompletedTask : RemoveRangeFromQueueAsync([songId]);

    private void FinalizeCurrentListenBeforeQueueRemoval()
    {
        if (!CurrentListenHistoryId.HasValue) return;

        // LibVLC resets its reported time when Stop is called, so capture the listen state
        // synchronously before the queue-removal path stops the underlying player.
        var sessionId = CurrentListenHistoryId.Value;
        var position = _audioPlayer.CurrentPosition;
        CurrentListenHistoryId = null;
        FireAndForgetSafe(
            () => _libraryService.FinalizeListenSessionAsync(sessionId, position, PlaybackEndReason.Skipped),
            "Finalizing session removed from queue",
            trackForDisposal: true);
    }

    private Guid? GetNextSurvivingSongId(HashSet<Guid> idsToRemove)
    {
        var activeQueue = IsShuffleEnabled ? _shuffledQueue : _playbackQueue;
        var currentIndex = IsShuffleEnabled ? CurrentShuffledIndex : CurrentQueueIndex;
        if (currentIndex < 0 || currentIndex >= activeQueue.Count) return null;

        for (var i = currentIndex + 1; i < activeQueue.Count; i++)
        {
            if (!idsToRemove.Contains(activeQueue[i])) return activeQueue[i];
        }

        if (CurrentRepeatMode != RepeatMode.RepeatAll) return null;

        for (var i = 0; i < currentIndex; i++)
        {
            if (!idsToRemove.Contains(activeQueue[i])) return activeQueue[i];
        }

        return null;
    }

    public Task RemoveRangeFromQueueAsync(IEnumerable<Guid> songIds)
    {
        var idsToRemove = songIds?.ToHashSet() ?? [];
        if (idsToRemove.Count == 0) return Task.CompletedTask;
        if (CurrentTrack == null || !idsToRemove.Contains(CurrentTrack.Id))
        {
            RemoveQueuedTracks(idsToRemove);
            return Task.CompletedTask;
        }

        var nextSongId = GetNextSurvivingSongId(idsToRemove);
        return RunPlaybackRequest(async request =>
        {
            FinalizeCurrentListenBeforeQueueRemoval();
            await StopInternalAsync(request);
            if (!IsCurrentRequest(request)) return;
            RemoveQueuedTracks(idsToRemove);
            if (nextSongId.HasValue && IsCurrentRequest(request))
            {
                var index = GetPlaybackQueueIndex(nextSongId.Value);
                if (index >= 0) await PlayQueueItemInternalAsync(index, request);
            }
        });
    }

    private void RemoveQueuedTracks(HashSet<Guid> idsToRemove)
    {
        _playbackQueue.RemoveAll(idsToRemove.Contains);
        _shuffledQueue.RemoveAll(idsToRemove.Contains);
        CommitQueueChanges();
    }

    public Task PlayQueueItemAsync(int originalQueueIndex) =>
        RunPlaybackRequest(request => PlayQueueItemInternalAsync(originalQueueIndex, request));

    private async Task PlayQueueItemInternalAsync(int originalQueueIndex, int request)
    {
        if (originalQueueIndex < 0 || originalQueueIndex >= _playbackQueue.Count)
        {
            await StopInternalAsync(request);
            return;
        }

        if (CurrentListenHistoryId.HasValue)
        {
            var previousSessionId = CurrentListenHistoryId.Value;
            var position = _audioPlayer.CurrentPosition;
            CurrentListenHistoryId = null;
            FireAndForgetSafe(() => _libraryService.FinalizeListenSessionAsync(previousSessionId, position, PlaybackEndReason.Skipped), "Finalizing skipped session", trackForDisposal: true);
        }

        _isEligibilityMarked = false;

        _isTransitioningTrack = true;

        // Use iterative approach with skip limit to prevent stack overflow if many
        // consecutive songs are deleted from disk but still in the queue
        const int maxSkipAttempts = 20;
        var currentIndex = originalQueueIndex;
        var skipCount = 0;
        Song? song = null;

        while (skipCount < maxSkipAttempts && currentIndex >= 0 && currentIndex < _playbackQueue.Count)
        {
            var songId = _playbackQueue[currentIndex];
            song = _transientTracks.GetValueOrDefault(songId)
                   ?? await _libraryService.GetSongByIdAsync(songId);
            if (!IsCurrentRequest(request)) return;
            currentIndex = GetPlaybackQueueIndex(songId);
            if (currentIndex < 0)
            {
                await StopInternalAsync(request);
                return;
            }

            if (song != null)
            {
                break;
            }

            _logger.LogWarning("Song metadata not found for ID {SongId}. Skipping to next track. (Skip {SkipCount}/{MaxSkip})",
                songId, skipCount + 1, maxSkipAttempts);
            skipCount++;

            currentIndex = GetAdjacentTrackIndex(currentIndex, moveForward: true);
        }

        if (song == null)
        {
            _logger.LogError("Failed to find playable track after skipping {SkipCount} deleted songs. Stopping playback.", skipCount);
            _isTransitioningTrack = false;
            await StopInternalAsync(request);
            return;
        }

        CurrentTrack = song;
        CurrentQueueIndex = currentIndex;
        PruneTransientTracks();

        if (!_transientTracks.ContainsKey(song.Id)) await StartListenSessionAsync(song.Id, request);
        if (!IsCurrentRequest(request)) return;

        CurrentShuffledIndex = IsShuffleEnabled ? GetShuffledQueueIndex(song.Id) : -1;
        _logger.LogDebug("Now playing '{SongTitle}' (Index: {QueueIndex}, Shuffled Index: {ShuffledIndex})",
            song.Title, CurrentQueueIndex, CurrentShuffledIndex);

        await _audioPlayer.LoadAsync(song);
        if (!IsCurrentRequest(request)) return;
        await ApplyReplayGainIfEnabledAsync();
        if (IsCurrentRequest(request)) await _audioPlayer.PlayAsync();
    }

    private async Task StartListenSessionAsync(Guid songId, int request)
    {
        // Presence listeners need the session before LoadAsync can raise MediaOpened.
        var startedUtc = DateTime.UtcNow;
        var sessionId = await _libraryService.StartListenSessionAsync(songId, _currentContext);
        if (!IsCurrentRequest(request))
        {
            if (sessionId.HasValue)
                await _libraryService.FinalizeListenSessionAsync(sessionId.Value, TimeSpan.Zero, PlaybackEndReason.PausedAndAbandoned);
            return;
        }

        CurrentListenHistoryId = sessionId;
        _currentListenStartedUtc = sessionId.HasValue ? startedUtc : null;
    }

    public Task ClearQueueAsync() =>
        RunPlaybackRequest(ClearQueueInternalAsync);

    private async Task ClearQueueInternalAsync(int request)
    {
        _queueVersion++;
        _logger.LogDebug("Clearing playback queue.");
        await StopInternalAsync(request);
        if (!IsCurrentRequest(request)) return;

        ClearQueuesInternal();
        CommitQueueChanges();
    }

    public async Task SavePlaybackStateAsync()
    {
        if (!_isInitialized) return;

        var playbackQueue = _playbackQueue.Where(id => !_transientTracks.ContainsKey(id)).ToList();
        var shuffledQueue = _shuffledQueue.Where(id => !_transientTracks.ContainsKey(id)).ToList();
        var currentTrackId = CurrentTrack is not null && !_transientTracks.ContainsKey(CurrentTrack.Id)
            ? CurrentTrack.Id
            : (Guid?)null;

        var state = new PlaybackState
        {
            CurrentTrackId = currentTrackId,
            ShuffleMode = CurrentShuffleMode,
            PlaybackQueueTrackIds = playbackQueue,
            CurrentPlaybackQueueIndex = playbackQueue.IndexOf(_playbackQueue.ElementAtOrDefault(CurrentQueueIndex)),
            ShuffledQueueTrackIds = IsShuffleEnabled ? shuffledQueue : [],
            CurrentShuffledQueueIndex = shuffledQueue.IndexOf(_shuffledQueue.ElementAtOrDefault(CurrentShuffledIndex))
        };
        await _settingsService.SavePlaybackStateAsync(state);
    }

    public Task SetEqualizerBandAsync(uint bandIndex, float gain)
    {
        var settings = CurrentEqualizerSettings;
        if (settings == null || bandIndex >= settings.BandGains.Count) return Task.CompletedTask;
        var gains = new List<float>(settings.BandGains);
        gains[(int)bandIndex] = gain;
        return ApplyEqualizerAsync(new EqualizerSettings { Preamp = settings.Preamp, BandGains = gains });
    }

    public Task SetEqualizerGainsAsync(IEnumerable<float> gains)
    {
        var settings = CurrentEqualizerSettings;
        if (settings == null) return Task.CompletedTask;
        var updatedGains = gains.Take(settings.BandGains.Count).ToList();
        updatedGains.AddRange(settings.BandGains.Skip(updatedGains.Count));
        return ApplyEqualizerAsync(new EqualizerSettings { Preamp = settings.Preamp, BandGains = updatedGains });
    }

    public EqualizerPreset? GetMatchingPreset(IEnumerable<float>? bandGains)
    {
        if (bandGains == null) return null;
        var currentGains = bandGains.ToArray();

        return AvailablePresets.FirstOrDefault(preset =>
            preset.Gains.Length == currentGains.Length &&
            preset.Gains.Zip(currentGains).All(pair => Math.Abs(pair.First - pair.Second) <= 0.1f));
    }

    public Task SetEqualizerPreampAsync(float gain)
    {
        var settings = CurrentEqualizerSettings;
        return settings == null ? Task.CompletedTask : ApplyEqualizerAsync(new EqualizerSettings
        {
            Preamp = gain,
            BandGains = new List<float>(settings.BandGains)
        });
    }

    public Task ResetEqualizerAsync() => CurrentEqualizerSettings == null
        ? Task.CompletedTask
        : ApplyEqualizerAsync(new EqualizerSettings
        {
            BandGains = Enumerable.Repeat(0.0f, CurrentEqualizerSettings.BandGains.Count).ToList()
        });

    private async Task ApplyEqualizerAsync(EqualizerSettings settings)
    {
        CurrentEqualizerSettings = settings;
        _audioPlayer.ApplyEqualizerSettings(settings);
        await _settingsService.SetEqualizerSettingsAsync(settings);
        EqualizerChanged?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed) return;
        Dispose();

        // Pending loads may still create sessions that need finalizing before dependencies are disposed.
        try
        {
            await AwaitPendingOperationsAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Pending finalization task failed during DisposeAsync.");
        }

        if (CurrentListenHistoryId.HasValue)
        {
            var sessionId = CurrentListenHistoryId.Value;
            var position = _audioPlayer.CurrentPosition;
            CurrentListenHistoryId = null;
            try
            {
                await _libraryService.FinalizeListenSessionAsync(sessionId, position, PlaybackEndReason.PausedAndAbandoned);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to finalize session during application exit.");
            }
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        _playbackRequestVersion++;
        _queueVersion++;
        _replayGainVersion++;

        _audioPlayer.PlaybackEnded -= OnAudioPlayerPlaybackEnded;
        _audioPlayer.StateChanged -= OnAudioPlayerStateChanged;
        _audioPlayer.VolumeChanged -= OnAudioPlayerVolumeChanged;
        _audioPlayer.PositionChanged -= OnAudioPlayerPositionChanged;
        _audioPlayer.ErrorOccurred -= OnAudioPlayerErrorOccurred;
        _audioPlayer.MediaOpened -= OnAudioPlayerMediaOpened;
        _audioPlayer.DurationChanged -= OnAudioPlayerDurationChanged;
        _audioPlayer.SmtcNextButtonPressed -= OnAudioPlayerSmtcNextButtonPressed;
        _audioPlayer.SmtcPreviousButtonPressed -= OnAudioPlayerSmtcPreviousButtonPressed;
        _settingsService.VolumeNormalizationEnabledChanged -= OnVolumeNormalizationEnabledChanged;
        _settingsService.FadeOnPlayPauseEnabledChanged -= OnFadeOnPlayPauseEnabledChanged;
        _settingsService.FadeInDurationChanged -= OnFadeInDurationChanged;
        _settingsService.FadeOutDurationChanged -= OnFadeOutDurationChanged;
    }

    private async Task<bool> RestoreInternalPlaybackStateAsync(PlaybackState state)
    {
        _logger.LogDebug("Attempting to restore previous playback state.");
        var savedQueue = state.PlaybackQueueTrackIds ?? [];
        _playbackQueue = savedQueue.Where(id => id != Guid.Empty).Distinct().ToList();
        RebuildPlaybackQueueIndex();

        if (!_playbackQueue.Any()) return false;
        CurrentShuffleMode = Enum.IsDefined(state.ShuffleMode) ? state.ShuffleMode : ShuffleMode.Random;

        if (IsShuffleEnabled)
        {
            _shuffledQueue = (state.ShuffledQueueTrackIds ?? Enumerable.Empty<Guid>()).ToList();

            // Equal lengths alone do not rule out duplicate or unrelated tracks.
            if (_shuffledQueue.Count != _playbackQueue.Count ||
                !_shuffledQueue.ToHashSet().SetEquals(_playbackQueue))
                GenerateShuffledQueue();
            RebuildShuffledQueueIndex();
        }

        if (state.CurrentTrackId.HasValue)
        {
            var currentSong = GetPlaybackQueueIndex(state.CurrentTrackId.Value) >= 0
                ? await _libraryService.GetSongByIdAsync(state.CurrentTrackId.Value)
                : null;
            if (_isDisposed) return false;
            if (currentSong != null)
            {
                CurrentTrack = currentSong;
                CurrentQueueIndex = GetPlaybackQueueIndex(currentSong.Id);
                if (IsShuffleEnabled)
                {
                    CurrentShuffledIndex = GetShuffledQueueIndex(currentSong.Id);
                }

                CurrentListenHistoryId = null;
                await _audioPlayer.LoadAsync(CurrentTrack);
            }
            else
            {
                // The current track is missing from the library or queue, but the queue is still valid.
                // Fall back to starting from the beginning of the queue rather than clearing it.
                _logger.LogWarning("Could not restore current track {SongId}: it is missing from the library or saved queue. Falling back to queue start.", state.CurrentTrackId.Value);
                CurrentQueueIndex = 0;
                CurrentShuffledIndex = IsShuffleEnabled ? 0 : -1;
                CurrentTrack = null;
                CurrentListenHistoryId = null;
            }
        }
        else
        {
            // Resolve saved positions by identity because removing duplicates can shift indices.
            var positionId = state.CurrentPlaybackQueueIndex >= 0 && state.CurrentPlaybackQueueIndex < savedQueue.Count
                ? savedQueue[state.CurrentPlaybackQueueIndex]
                : Guid.Empty;
            CurrentQueueIndex = Math.Max(0, GetPlaybackQueueIndex(positionId));
            CurrentShuffledIndex = -1;
            if (IsShuffleEnabled)
            {
                var savedShuffle = state.ShuffledQueueTrackIds ?? [];
                var shuffledPositionId = state.CurrentShuffledQueueIndex >= 0 && state.CurrentShuffledQueueIndex < savedShuffle.Count
                    ? savedShuffle[state.CurrentShuffledQueueIndex]
                    : Guid.Empty;
                CurrentShuffledIndex = GetShuffledQueueIndex(shuffledPositionId);
                if (CurrentShuffledIndex < 0)
                    CurrentShuffledIndex = GetShuffledQueueIndex(_playbackQueue[CurrentQueueIndex]);
                CurrentQueueIndex = GetPlaybackQueueIndex(_shuffledQueue[CurrentShuffledIndex]);
            }
        }

        return true;
    }

    private void ClearQueuesInternal()
    {
        _queueVersion++;
        _playbackQueue.Clear();
        _shuffledQueue.Clear();
        _playbackQueueIndex.Clear();
        _shuffledQueueIndex.Clear();
        _transientTracks.Clear();
        _transientTrackIdsByPath.Clear();
        CurrentTrack = null;
        CurrentQueueIndex = -1;
        CurrentShuffledIndex = -1;
        CurrentListenHistoryId = null;
        _currentListenStartedUtc = null;
        CurrentShuffleMode = ShuffleMode.Random;
    }

    private void GenerateShuffledQueue()
    {
        _shuffledQueue = new List<Guid>(_playbackQueue);
        Random.Shared.Shuffle(CollectionsMarshal.AsSpan(_shuffledQueue));
    }

    private void RebuildPlaybackQueueIndex()
    {
        _playbackQueueIndex.Clear();
        for (var i = 0; i < _playbackQueue.Count; i++)
        {
            _playbackQueueIndex[_playbackQueue[i]] = i;
        }
    }

    private void RebuildShuffledQueueIndex()
    {
        _shuffledQueueIndex.Clear();
        for (var i = 0; i < _shuffledQueue.Count; i++)
        {
            _shuffledQueueIndex[_shuffledQueue[i]] = i;
        }
    }

    private int GetPlaybackQueueIndex(Guid songId) => _playbackQueueIndex.GetValueOrDefault(songId, -1);

    private int GetShuffledQueueIndex(Guid songId) => _shuffledQueueIndex.GetValueOrDefault(songId, -1);

    private int GetAdjacentTrackIndex(int originalIndex, bool moveForward)
    {
        if (originalIndex < 0 || originalIndex >= _playbackQueue.Count) return -1;
        var queue = IsShuffleEnabled ? _shuffledQueue : _playbackQueue;
        var index = IsShuffleEnabled ? GetShuffledQueueIndex(_playbackQueue[originalIndex]) : originalIndex;
        if (index < 0) return -1;

        var nextIndex = index + (moveForward ? 1 : -1);
        if (nextIndex < 0 || nextIndex >= queue.Count)
        {
            if (CurrentRepeatMode != RepeatMode.RepeatAll) return -1;
            nextIndex = moveForward ? 0 : queue.Count - 1;
        }

        return IsShuffleEnabled ? GetPlaybackQueueIndex(queue[nextIndex]) : nextIndex;
    }

    private void UpdateSmtcControls()
    {
        var canGoNext = false;
        var canGoPrevious = false;

        if (_playbackQueue.Any())
        {
            if (CurrentRepeatMode != RepeatMode.Off)
            {
                canGoNext = true;
                canGoPrevious = true;
            }
            else if (IsShuffleEnabled)
            {
                canGoNext = CurrentShuffledIndex < _shuffledQueue.Count - 1;
                canGoPrevious = CurrentShuffledIndex > 0;
            }
            else
            {
                canGoNext = CurrentQueueIndex < _playbackQueue.Count - 1;
                canGoPrevious = CurrentQueueIndex > 0;
            }

            // If a track is active, you can always go "previous" to either restart the track or go to the prior one.
            if (CurrentTrack != null) canGoPrevious = true;
        }

        _audioPlayer.UpdateSmtcButtonStates(canGoNext, canGoPrevious);
    }

    private Task RunPlaybackRequest(Func<int, Task> action)
    {
        if (_isDisposed) return Task.CompletedTask;
        var request = ++_playbackRequestVersion;
        var task = ExecuteAsync();
        TrackPendingOperation(task);
        return task;

        async Task ExecuteAsync()
        {
            try
            {
                await action(request);
            }
            finally
            {
                if (IsCurrentRequest(request)) _isTransitioningTrack = false;
            }
        }
    }

    private bool IsCurrentRequest(int request) => !_isDisposed && request == _playbackRequestVersion;

    private void FireAndForgetSafe(Func<Task> asyncAction, string operationName, bool trackForDisposal = false)
    {
        var task = ExecuteAsync();

        if (trackForDisposal)
            TrackPendingOperation(task);

        async Task ExecuteAsync()
        {
            try
            {
                await asyncAction();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in fire-and-forget operation: {Operation}", operationName);
            }
        }
    }

    private void TrackPendingOperation(Task task)
    {
        lock (_pendingOperationsLock)
        {
            _pendingOperations.Add(task);
        }

        _ = task.ContinueWith(
            _ =>
            {
                lock (_pendingOperationsLock)
                {
                    _pendingOperations.Remove(task);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private Task AwaitPendingOperationsAsync()
    {
        Task[] pending;
        lock (_pendingOperationsLock)
        {
            if (_pendingOperations.Count == 0)
                return Task.CompletedTask;

            pending = _pendingOperations.ToArray();
        }

        return Task.WhenAll(pending);
    }

    internal Task FlushPendingFinalizationAsync() => AwaitPendingOperationsAsync();

    private void OnAudioPlayerPlaybackEnded()
    {
        if (_isTransitioningTrack) return;
        // Capture and clear the session ID synchronously before firing the async task.
        // This prevents PlayQueueItemAsync (called inside NextAsync) from seeing a non-null
        // CurrentListenHistoryId and issuing a second FinalizeListenSessionAsync(Skipped)
        // on a session already being finalized here as Finished.
        var finishedSessionId = CurrentListenHistoryId;
        var duration = Duration;
        var request = _playbackRequestVersion;
        CurrentListenHistoryId = null;

        FireAndForgetSafe(async () =>
        {
            if (finishedSessionId.HasValue)
            {
                await _libraryService.FinalizeListenSessionAsync(finishedSessionId.Value, duration, PlaybackEndReason.Finished);
            }
            if (IsCurrentRequest(request)) await NextAsync();
        }, "PlaybackEnded transition", trackForDisposal: true);
    }

    private void OnAudioPlayerStateChanged()
    {
        PlaybackStateChanged?.Invoke();
    }

    private void OnAudioPlayerVolumeChanged()
    {
        FireAndForgetSafe(
            async () =>
            {
                await _settingsService.SaveVolumeAsync(_audioPlayer.Volume);
                VolumeStateChanged?.Invoke();
            },
            "Volume save");
    }

    private void OnAudioPlayerPositionChanged()
    {
        PositionChanged?.Invoke();
        MaybeMarkEligibleForScrobbling();
    }

    private void MaybeMarkEligibleForScrobbling()
    {
        if (_isTransitioningTrack || _isEligibilityMarked || !CurrentListenHistoryId.HasValue) return;

        var duration = Duration;
        var progress = _audioPlayer.CurrentPosition;

        // Apply the scrobble threshold even when no external scrobbling account is configured.
        var isLongEnough = duration.TotalSeconds > 30;
        var hasPlayedEnough = progress.TotalSeconds >= duration.TotalSeconds / 2
                           || progress.TotalMinutes >= 4;

        if (!isLongEnough || !hasPlayedEnough) return;

        // Prevent re-entry within the same listening session.
        _isEligibilityMarked = true;
        var sessionId = CurrentListenHistoryId.Value;
        var song = CurrentTrack!;
        var listenStartedUtc = _currentListenStartedUtc ?? DateTime.UtcNow - progress;
        FireAndForgetSafe(
            async () =>
            {
                var marked = false;
                try
                {
                    marked = await _libraryService.MarkListenAsEligibleForScrobblingAsync(sessionId);
                    if (marked && !_isDisposed) ScrobbleEligibilityReached?.Invoke(song, sessionId, listenStartedUtc);
                }
                finally
                {
                    if (!marked && CurrentListenHistoryId == sessionId) _isEligibilityMarked = false;
                }
            },
            "Mark listen eligible for scrobbling",
            trackForDisposal: true);
    }

    private void OnAudioPlayerDurationChanged()
    {
        DurationChanged?.Invoke();
    }

    private void OnAudioPlayerErrorOccurred(string errorMessage)
    {
        _logger.LogWarning("Audio playback failed: {Message}", errorMessage);
        FireAndForgetSafe(StopAsync, "Error recovery");
    }

    private void OnAudioPlayerMediaOpened()
    {
        TrackChanged?.Invoke();
        UpdateSmtcControls();
    }

    private void OnAudioPlayerSmtcNextButtonPressed()
    {
        FireAndForgetSafe(
            NextAsync,
            "SMTC Next");
    }

    private void OnAudioPlayerSmtcPreviousButtonPressed()
    {
        FireAndForgetSafe(
            PreviousAsync,
            "SMTC Previous");
    }

    private async Task ApplyReplayGainIfEnabledAsync(bool? isEnabledOverride = null)
    {
        var version = ++_replayGainVersion;
        var request = _playbackRequestVersion;
        var song = CurrentTrack;
        if (song == null) return;
        var isEnabled = isEnabledOverride ?? await _settingsService.GetVolumeNormalizationEnabledAsync();
        if (!IsCurrentRequest(request) || version != _replayGainVersion) return;

        if (isEnabled && !_transientTracks.ContainsKey(song.Id) &&
            !song.ReplayGainTrackGain.HasValue && !song.ReplayGainCheckPerformed)
        {
            var refreshed = await _libraryService.GetSongByIdAsync(song.Id);
            if (!IsCurrentRequest(request) || version != _replayGainVersion) return;
            song.ReplayGainCheckPerformed = true;
            if (refreshed != null)
            {
                song.ReplayGainTrackGain = refreshed.ReplayGainTrackGain;
                song.ReplayGainTrackPeak = refreshed.ReplayGainTrackPeak;
            }
        }

        var gain = isEnabled ? song.ReplayGainTrackGain.GetValueOrDefault() : 0;
        if (isEnabled && song.ReplayGainTrackGain.HasValue && song.ReplayGainTrackPeak is > 0)
            gain = Math.Min(gain, -20 * Math.Log10(song.ReplayGainTrackPeak.Value));
        await _audioPlayer.SetReplayGainAsync(gain);
    }

    private void OnVolumeNormalizationEnabledChanged(bool isEnabled)
    {
        FireAndForgetSafe(
            () => ApplyReplayGainIfEnabledAsync(isEnabled),
            "ReplayGain settings change", trackForDisposal: true);
    }

    private void OnFadeOnPlayPauseEnabledChanged(bool isEnabled)
    {
        _audioPlayer.SetFadeOnPlayPauseEnabled(isEnabled);
    }

    private void OnFadeInDurationChanged(int durationMs)
    {
        _audioPlayer.SetFadeInDuration(durationMs);
    }

    private void OnFadeOutDurationChanged(int durationMs)
    {
        _audioPlayer.SetFadeOutDuration(durationMs);
    }
}
