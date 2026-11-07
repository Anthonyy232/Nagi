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
    ///     The song playing or paused, or null while stopped.
    /// </summary>
    Song? CurrentTrack { get; }

    ShuffleMode CurrentShuffleMode { get; }
    PlaybackStopMode StopMode { get; }

    /// <summary>
    ///     How long until the <see cref="PlaybackStopMode.Timer" /> stops playback, or null while no timer is set.
    /// </summary>
    TimeSpan? StopTimeRemaining { get; }

    event Action? StopScheduleChanged;
    Task<bool> PlayMixAsync(IEnumerable<Guid> songIds, ShuffleMode mode, TimeSpan? duration = null, PlaybackContext? context = null, int avoidRecentDays = PlaybackMixLimits.DefaultAvoidRecentDays);

    /// <param name="delay">
    ///     For <see cref="PlaybackStopMode.Timer" />, up to <see cref="PlaybackMixLimits.MaxMinutes" />.
    /// </param>
    /// <param name="finishCurrentTrack">Whether a timer that runs out mid-song lets the song finish first.</param>
    void ScheduleStop(PlaybackStopMode mode, TimeSpan? delay = null, bool finishCurrentTrack = true);

    /// <summary>
    ///     The ListenHistory entry of the current listen, or null while none is being recorded.
    /// </summary>
    long? CurrentListenHistoryId { get; }

    bool IsPlaying { get; }

    /// <summary>
    ///     True while the current song is still opening and hasn't started, such as while its music server answers.
    ///     <see cref="PlaybackStateChanged" /> occurs when this changes.
    /// </summary>
    bool IsOpening { get; }

    TimeSpan CurrentPosition { get; }

    TimeSpan Duration { get; }

    /// <summary>From 0.0 to 1.0.</summary>
    double Volume { get; }

    bool IsMuted { get; }

    /// <summary>
    ///     The queue in the order it plays, shuffled while <see cref="IsShuffleEnabled" />.
    /// </summary>
    IReadOnlyList<Guid> PlayOrder { get; }

    /// <summary>
    ///     <see cref="PlayOrder" /> from the current song, or the song playback resumes with while stopped, carrying on
    ///     from the start while repeating, as <see cref="NextAsync" /> goes through it.
    /// </summary>
    IReadOnlyList<Guid> UpcomingQueue { get; }

    /// <summary>The song <see cref="NextAsync" /> plays, or null when it would stop.</summary>
    Guid? NextTrackId { get; }

    bool IsShuffleEnabled { get; }

    RepeatMode CurrentRepeatMode { get; }

    /// <summary>Whether <see cref="NextAsync" /> has a track to go to. Changes come with the queue, track, shuffle and repeat events.</summary>
    bool CanGoNext { get; }

    /// <summary>Whether <see cref="PreviousAsync" /> has a track to go to or restart.</summary>
    bool CanGoPrevious { get; }

    /// <summary>
    ///     Occurs when the current track changes or playback stops. A new track's listen, if one is recorded, has
    ///     already started, so <see cref="CurrentListenHistoryId" /> belongs to it.
    /// </summary>
    event Action? TrackChanged;

    event Action? PlaybackStateChanged;

    event Action? VolumeStateChanged;

    event Action? ShuffleModeChanged;

    event Action? RepeatModeChanged;

    event Action? QueueChanged;

    event Action? PositionChanged;

    event Action? DurationChanged;

    /// <summary>
    ///     Occurs once per listening session when the current track has been played
    ///     long enough to be eligible for scrobbling (track &gt; 30 s AND played ≥ 50% or ≥ 4 min).
    ///     Subscribers receive the song, the listen history session ID and the UTC time the listen started.
    /// </summary>
    event Action<Song, long, DateTime>? ScrobbleEligibilityReached;

    /// <summary>
    ///     Occurs when a song from a music server couldn't be streamed and playback stopped. May be raised on a
    ///     background thread.
    /// </summary>
    event Action<Song>? ServerSongFailed;

    /// <summary>
    ///     Loads the saved volume, shuffle and repeat, and restores the last session when asked to and the user
    ///     hasn't turned restoring off.
    /// </summary>
    Task InitializeAsync(bool restoreLastSession = true);

    /// <summary>
    ///     Plays a single song, replacing the queue.
    /// </summary>
    Task PlayAsync(Guid songId);

    /// <summary>
    ///     Plays songs, replacing the queue.
    /// </summary>
    /// <param name="startShuffled">
    ///     True to shuffle, letting the shuffle choose the first song; false to play in order; null to keep the shuffle
    ///     setting, playing the song at <paramref name="startIndex" /> first either way.
    /// </param>
    /// <param name="context">Where the songs come from, for listen history. Defaults to the library.</param>
    Task PlayAsync(IEnumerable<Guid> songIds, int startIndex = 0, bool? startShuffled = null, PlaybackContext? context = null);

    /// <summary>
    ///     Toggles between playing and pausing the current track. If no track is loaded, it starts the current queue.
    /// </summary>
    Task PlayPauseAsync();

    /// <summary>
    ///     Skips to the next song in play order, going round the queue while repeating. Repeating one song only
    ///     replays it when it ends, and stopping after the current track waits for it to end too.
    /// </summary>
    Task NextAsync();

    /// <summary>
    ///     Restarts the current track once it has played a few seconds, otherwise goes to the previous track.
    /// </summary>
    Task PreviousAsync();

    Task SeekAsync(TimeSpan position);

    Task PlayAlbumAsync(Guid albumId);

    Task PlayArtistAsync(Guid artistId);

    Task PlayFolderAsync(Guid folderId);

    Task PlayPlaylistAsync(Guid playlistId);

    /// <returns>False when the smart playlist matches no songs, so nothing plays.</returns>
    Task<bool> PlaySmartPlaylistAsync(Guid smartPlaylistId);

    Task PlayGenreAsync(Guid genreId);

    Task ShuffleLibraryAsync();

    /// <summary>
    ///     Replaces the queue with a single audio file and plays it. A file in the library plays as that library song.
    /// </summary>
    Task PlayTransientFileAsync(string filePath);

    /// <summary>
    ///     Adds audio files that are not necessarily in the library to the end of the queue. A folder
    ///     adds the audio files inside it, including subfolders, in path order.
    /// </summary>
    Task AddTransientFilesToQueueAsync(IEnumerable<string> paths);

    /// <summary>
    ///     Sets the player volume and persists the value.
    /// </summary>
    /// <param name="volume">From 0.0 to 1.0.</param>
    Task SetVolumeAsync(double volume);

    /// <summary>
    ///     Toggles the mute state of the player and persists the value.
    /// </summary>
    Task ToggleMuteAsync();

    /// <summary>
    ///     The playback speed for this session. 1.0 is normal speed.
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
    ///     Adds the songs that aren't queued yet to the end of the queue.
    /// </summary>
    Task AddRangeToQueueAsync(IEnumerable<Guid> songIds);

    /// <summary>
    ///     Places the songs, in the order given, right after the current track, queueing any that aren't.
    /// </summary>
    Task PlayNextAsync(IEnumerable<Guid> songIds);

    /// <summary>
    ///     Removes a song from the queue. If the removed song was playing, playback advances to the next song.
    /// </summary>
    Task RemoveFromQueueAsync(Guid songId);

    /// <summary>
    ///     Plays a queued song, keeping the queue.
    /// </summary>
    Task PlayQueueItemAsync(Guid songId);

    /// <summary>
    ///     Stops playback and clears the entire queue.
    /// </summary>
    Task ClearQueueAsync();

    /// <summary>
    ///     Moves a song within <see cref="UpcomingQueue" />, before another or, when <paramref name="beforeSongId" /> is
    ///     null, to its end. Its first song is the current one, so it can't be moved and nothing can go before it.
    /// </summary>
    /// <returns>Whether the song moved.</returns>
    bool MoveQueueItem(Guid songId, Guid? beforeSongId);

    /// <summary>
    ///     The queued songs, whether from the library or from files outside it, by ID. Songs no longer in the library
    ///     are left out.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, Song>> GetQueueSongsAsync(IEnumerable<Guid> songIds, CancellationToken cancellationToken = default);

    Task<TimeSpan> GetQueueDurationAsync(IEnumerable<Guid> songIds, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Saves the queue and current track for the next launch, or clears the saved session when the user turned
    ///     restoring it off.
    /// </summary>
    Task SavePlaybackStateAsync();
}
