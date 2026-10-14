using Nagi.Core.Models;
using Nagi.Core.Services.Data;

namespace Nagi.Core.Services.Abstractions;

/// <remarks>
///     Call mutations from the UI context. New transport requests supersede pending selections;
///     dispose asynchronously to finish outstanding listen-history writes.
/// </remarks>
public interface IMusicPlaybackService : IDisposable, IAsyncDisposable
{
    /// <summary>
    ///     Gets the currently playing song, or null if nothing is playing or the player is stopped.
    /// </summary>
    Song? CurrentTrack { get; }
    ShuffleMode CurrentShuffleMode { get; }
    PlaybackStopMode StopMode { get; }
    DateTimeOffset? StopDeadline { get; }
    event Action? StopScheduleChanged;
    Task<bool> PlayMixAsync(IEnumerable<Guid> songIds, ShuffleMode mode, TimeSpan? duration = null, PlaybackContext? context = null, int avoidRecentDays = 1);
    void ScheduleStop(PlaybackStopMode mode, TimeSpan? delay = null, bool finishCurrentTrack = true);
    Task CheckStopScheduleAsync();

    /// <summary>
    ///     Gets the unique database ID for the current listening session.
    ///     This is used to link playback events to a specific entry in the ListenHistory table.
    /// </summary>
    long? CurrentListenHistoryId { get; }

    bool IsPlaying { get; }

    TimeSpan CurrentPosition { get; }

    TimeSpan Duration { get; }

    /// <summary>
    ///     Gets the current volume level (0.0 to 1.0).
    /// </summary>
    double Volume { get; }

    bool IsMuted { get; }

    /// <summary>
    ///     Gets a read-only list of song IDs in the original, unshuffled playback queue.
    /// </summary>
    IReadOnlyList<Guid> PlaybackQueue { get; }

    /// <summary>
    ///     Gets a read-only list of song IDs in the shuffled playback queue. This list is empty if shuffle is disabled.
    /// </summary>
    IReadOnlyList<Guid> ShuffledQueue { get; }

    int CurrentQueueIndex { get; }

    /// <summary>
    ///     Gets the index of the current track in the <see cref="ShuffledQueue" />.
    ///     Returns -1 if shuffle is disabled or no track is playing.
    /// </summary>
    int CurrentShuffledIndex { get; }


    bool IsShuffleEnabled { get; }

    RepeatMode CurrentRepeatMode { get; }

    IReadOnlyList<(uint Index, float Frequency)> EqualizerBands { get; }

    EqualizerSettings? CurrentEqualizerSettings { get; }

    /// <summary>
    ///     Occurs when the current track changes or playback stops.
    /// </summary>
    event Action? TrackChanged;

    event Action? PlaybackStateChanged;

    event Action? VolumeStateChanged;

    event Action? ShuffleModeChanged;

    event Action? RepeatModeChanged;

    event Action? QueueChanged;

    event Action? PositionChanged;

    event Action? DurationChanged;

    event Action? EqualizerChanged;

    /// <summary>
    ///     Occurs once per listening session when the current track has been played
    ///     long enough to be eligible for scrobbling (track &gt; 30 s AND played ≥ 50% or ≥ 4 min).
    ///     Subscribers receive the song and the listen history session ID.
    /// </summary>
    event Action<Song, long, DateTime>? ScrobbleEligibilityReached;

    /// <summary>
    ///     Initializes the service, loading settings and optionally restoring the last saved playback state.
    /// </summary>
    Task InitializeAsync(bool restoreLastSession = true);

    /// <summary>
    ///     Plays a single song, replacing the current queue.
    /// </summary>
    Task PlayAsync(Guid songId);

    /// <summary>
    ///     Plays a single song, replacing the current queue.
    /// </summary>
    Task PlayAsync(Song song);

    /// <summary>
    ///     Plays a list of songs, replacing the current queue.
    /// </summary>
    /// <param name="startShuffled">If true, shuffle will be enabled; if false, disabled; if null, preserved.</param>
    Task PlayAsync(IEnumerable<Song> songs, int startIndex = 0, bool? startShuffled = null);

    /// <summary>
    ///     Plays a list of songs by their IDs, replacing the current queue.
    /// </summary>
    /// <param name="startShuffled">If true, shuffle will be enabled; if false, disabled; if null, preserved.</param>
    /// <param name="context">The playback context (source) for listen history tracking. Defaults to Library.</param>
    Task PlayAsync(IEnumerable<Guid> songIds, int startIndex = 0, bool? startShuffled = null, PlaybackContext? context = null);

    /// <summary>
    ///     Toggles between playing and pausing the current track. If no track is loaded, it starts the current queue.
    /// </summary>
    Task PlayPauseAsync();

    /// <summary>
    ///     Stops playback but preserves the current queue and index.
    /// </summary>
    Task StopAsync();

    /// <summary>
    ///     Skips to the next track in the queue, respecting shuffle and repeat settings.
    /// </summary>
    Task NextAsync();

    /// <summary>
    ///     Goes to the previous track in the queue or restarts the current track, respecting shuffle and repeat settings.
    /// </summary>
    Task PreviousAsync();

    Task SeekAsync(TimeSpan position);

    /// <summary>
    ///     Creates a new queue from all songs in the specified album and starts playback.
    /// </summary>
    Task PlayAlbumAsync(Guid albumId);

    /// <summary>
    ///     Creates a new queue from all songs by the specified artist and starts playback.
    /// </summary>
    Task PlayArtistAsync(Guid artistId);

    /// <summary>
    ///     Creates a new queue from all songs in the specified folder and starts playback.
    /// </summary>
    Task PlayFolderAsync(Guid folderId);

    /// <summary>
    ///     Creates a new queue from the specified playlist and starts playback.
    /// </summary>
    Task PlayPlaylistAsync(Guid playlistId);

    /// <summary>
    ///     Creates a new queue from all songs of the specified genre and starts playback.
    /// </summary>
    Task PlayGenreAsync(Guid genreId);

    /// <summary>
    ///     Creates a shuffled queue from the whole library and starts playback.
    /// </summary>
    Task ShuffleLibraryAsync();

    /// <summary>
    ///     Plays a single audio file directly from its path, clearing the current queue.
    ///     The file does not need to be in the library.
    /// </summary>
    Task PlayTransientFileAsync(string filePath);

    /// <summary>
    ///     Adds audio files that are not necessarily in the library to the end of the queue. A folder
    ///     adds the audio files inside it, including subfolders, in path order.
    /// </summary>
    Task AddTransientFilesToQueueAsync(IEnumerable<string> paths);

    /// <summary>
    ///     Resolves library and transient tracks for queue display.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, Song>> GetQueueTracksAsync(IEnumerable<Guid> songIds);

    /// <summary>
    ///     Sets the player volume and persists the value.
    /// </summary>
    /// <param name="volume">The new volume level (0.0 to 1.0).</param>
    Task SetVolumeAsync(double volume);

    /// <summary>
    ///     Toggles the mute state of the player and persists the value.
    /// </summary>
    Task ToggleMuteAsync();

    /// <summary>
    ///     Gets the playback speed for this session. 1.0 is normal speed.
    /// </summary>
    double PlaybackRate { get; }

    /// <summary>
    ///     Sets the playback speed for this session. It is not persisted.
    /// </summary>
    Task SetPlaybackRateAsync(double rate);

    /// <summary>
    ///     Enables or disables shuffle mode and persists the setting.
    /// </summary>
    Task SetShuffleAsync(bool enable);

    /// <summary>
    ///     Sets the repeat mode and persists the setting.
    /// </summary>
    Task SetRepeatModeAsync(RepeatMode mode);

    /// <summary>
    ///     Adds a song to the end of the current queue.
    /// </summary>
    Task AddToQueueAsync(Guid songId);

    /// <summary>
    ///     Adds a song to the end of the current queue.
    /// </summary>
    Task AddToQueueAsync(Song song);

    /// <summary>
    ///     Adds a collection of songs to the end of the current queue.
    /// </summary>
    Task AddRangeToQueueAsync(IEnumerable<Song> songs);

    /// <summary>
    ///     Adds a collection of song IDs to the end of the current queue.
    /// </summary>
    Task AddRangeToQueueAsync(IEnumerable<Guid> songIds);

    /// <summary>
    ///     Inserts a song into the queue immediately after the current track.
    /// </summary>
    Task PlayNextAsync(Song song);

    /// <summary>
    ///     Inserts a song into the queue immediately after the current track.
    /// </summary>
    Task PlayNextAsync(Guid songId);

    /// <summary>
    ///     Removes a song from the queue. If the removed song was playing, playback advances to the next song.
    /// </summary>
    Task RemoveFromQueueAsync(Guid songId);

    /// <summary>
    ///     Removes a song from the queue. If the removed song was playing, playback advances to the next song.
    /// </summary>
    Task RemoveFromQueueAsync(Song song);

    /// <summary>
    ///     Removes multiple songs from the queue by their IDs.
    /// </summary>
    Task RemoveRangeFromQueueAsync(IEnumerable<Guid> songIds);

    /// <summary>
    ///     Jumps to and plays a specific item in the queue.
    /// </summary>
    /// <param name="originalQueueIndex">The index of the song in the original, unshuffled queue.</param>
    Task PlayQueueItemAsync(int originalQueueIndex);

    /// <summary>
    ///     Stops playback and clears the entire queue.
    /// </summary>
    Task ClearQueueAsync();

    void MoveQueueItem(Guid songId, Guid? beforeSongId);
    Task<IReadOnlyDictionary<Guid, Song>> GetQueueSongsAsync(IEnumerable<Guid> songIds, CancellationToken cancellationToken = default);
    Task<TimeSpan> GetQueueDurationAsync(IEnumerable<Guid> songIds, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Saves the current playback state (queue, track, position) to persistent storage.
    /// </summary>
    Task SavePlaybackStateAsync();

    /// <summary>
    ///     Sets the gain for a specific band index.
    /// </summary>
    /// <param name="gain">The new gain in decibels (-20 to 20).</param>
    Task SetEqualizerBandAsync(uint bandIndex, float gain);

    /// <summary>
    ///     Sets the gain values for all equalizer bands at once.
    /// </summary>
    /// <param name="gains">A collection of gain values in decibels, one for each band.</param>
    Task SetEqualizerGainsAsync(IEnumerable<float> gains);

    IReadOnlyList<EqualizerPreset> AvailablePresets { get; }

    /// <summary>
    ///     Finds a preset that matches the given band gain values.
    /// </summary>
    /// <returns>The matching preset, or null if no preset matches the current settings.</returns>
    EqualizerPreset? GetMatchingPreset(IEnumerable<float> bandGains);

    /// <summary>
    ///     Sets the pre-amplification level for the equalizer.
    /// </summary>
    /// <param name="gain">The new gain in decibels (-20 to 20).</param>
    Task SetEqualizerPreampAsync(float gain);

    /// <summary>
    ///     Resets the equalizer to a flat state (all gains at 0).
    /// </summary>
    Task ResetEqualizerAsync();
}
