using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Nagi.Models;

namespace Nagi.Services;

/// <summary>
///     Defines the available sorting orders for a list of songs.
/// </summary>
public enum SongSortOrder
{
    TitleAsc,
    TitleDesc,
    DateAddedDesc,
    DateAddedAsc,
    AlbumAsc,
    ArtistAsc,
    TrackNumberAsc
}

/// <summary>
///     Defines playback repeat modes.
/// </summary>
public enum RepeatMode
{
    Off,
    RepeatOne,
    RepeatAll
}

/// <summary>
///     Defines the contract for a high-level music playback service,
///     managing the playback queue, playback state, and user settings like shuffle and repeat.
/// </summary>
public interface IMusicPlaybackService
{
    #region Events

    /// <summary>
    ///     Occurs when the currently playing track changes.
    /// </summary>
    event Action? TrackChanged;

    /// <summary>
    ///     Occurs when the playback state (e.g., IsPlaying) changes.
    /// </summary>
    event Action? PlaybackStateChanged;

    /// <summary>
    ///     Occurs when the volume or mute state changes.
    /// </summary>
    event Action? VolumeStateChanged;

    /// <summary>
    ///     Occurs when shuffle mode is enabled or disabled.
    /// </summary>
    event Action? ShuffleModeChanged;

    /// <summary>
    ///     Occurs when the repeat mode changes.
    /// </summary>
    event Action? RepeatModeChanged;

    /// <summary>
    ///     Occurs when the content or order of the playback queue changes.
    /// </summary>
    event Action? QueueChanged;

    /// <summary>
    ///     Occurs frequently as the playback position changes during active playback.
    /// </summary>
    event Action? PositionChanged;

    #endregion

    #region Properties

    /// <summary>
    ///     Gets the currently playing or paused track. Returns null if nothing is loaded.
    /// </summary>
    Song? CurrentTrack { get; }

    /// <summary>
    ///     Gets a value indicating whether a track is currently playing.
    /// </summary>
    bool IsPlaying { get; }

    /// <summary>
    ///     Gets the current playback position of the current track.
    /// </summary>
    TimeSpan CurrentPosition { get; }

    /// <summary>
    ///     Gets the total duration of the current track.
    /// </summary>
    TimeSpan Duration { get; }

    /// <summary>
    ///     Gets the current playback volume (0.0 to 1.0).
    /// </summary>
    double Volume { get; }

    /// <summary>
    ///     Gets a value indicating whether playback is currently muted.
    /// </summary>
    bool IsMuted { get; }

    /// <summary>
    ///     Gets a value indicating if the service is in the process of changing tracks.
    /// </summary>
    bool IsTransitioningTrack { get; }

    /// <summary>
    ///     Gets a read-only list of songs in the original, non-shuffled playback queue.
    /// </summary>
    IReadOnlyList<Song> PlaybackQueue { get; }

    /// <summary>
    ///     Gets a read-only list of songs in the shuffled playback queue. This list is empty if shuffle is disabled.
    /// </summary>
    IReadOnlyList<Song> ShuffledQueue { get; }

    /// <summary>
    ///     Gets the index of the current track within the original, non-shuffled PlaybackQueue.
    ///     Returns -1 if no track is active.
    /// </summary>
    int CurrentQueueIndex { get; }

    /// <summary>
    ///     Gets a value indicating whether shuffle mode is currently enabled.
    /// </summary>
    bool IsShuffleEnabled { get; }

    /// <summary>
    ///     Gets the current repeat mode.
    /// </summary>
    RepeatMode CurrentRepeatMode { get; }

    #endregion

    #region Playback Control

    /// <summary>
    ///     Initializes the playback service, loading persisted settings and restoring the last playback state.
    /// </summary>
    Task InitializeAsync();

    /// <summary>
    ///     Plays a collection of songs, replacing the current queue.
    /// </summary>
    /// <param name="songs">The collection of songs to play.</param>
    /// <param name="startIndex">The index of the song to start with. Ignored if startShuffled is true.</param>
    /// <param name="startShuffled">If true, shuffle is enabled and a random song starts playback.</param>
    Task PlayAsync(IEnumerable<Song> songs, int startIndex = 0, bool startShuffled = false);

    /// <summary>
    ///     Plays a single song, replacing the current queue.
    /// </summary>
    /// <param name="song">The song to play.</param>
    Task PlayAsync(Song song);

    /// <summary>
    ///     Toggles between play and pause. If no track is active, it starts the current queue.
    /// </summary>
    Task PlayPauseAsync();

    /// <summary>
    ///     Stops playback and unloads the current track.
    /// </summary>
    Task StopAsync();

    /// <summary>
    ///     Advances to the next track in the queue, respecting shuffle and repeat modes.
    /// </summary>
    Task NextAsync();

    /// <summary>
    ///     Moves to the previous track. If the current track has played for more than a few seconds, it restarts the current
    ///     track instead.
    /// </summary>
    Task PreviousAsync();

    /// <summary>
    ///     Seeks to a specific position within the current track.
    /// </summary>
    /// <param name="position">The desired playback position.</param>
    Task SeekAsync(TimeSpan position);

    #endregion

    #region Mode and Volume Control

    /// <summary>
    ///     Sets the playback volume and persists the value.
    /// </summary>
    /// <param name="volume">Volume level from 0.0 (silent) to 1.0 (max).</param>
    Task SetVolumeAsync(double volume);

    /// <summary>
    ///     Toggles the mute state and persists the change.
    /// </summary>
    Task ToggleMuteAsync();

    /// <summary>
    ///     Enables or disables shuffle mode and persists the change.
    /// </summary>
    /// <param name="enable">True to enable shuffle, false to disable.</param>
    Task SetShuffleAsync(bool enable);

    /// <summary>
    ///     Sets the repeat mode for playback and persists the change.
    /// </summary>
    /// <param name="mode">The desired repeat mode.</param>
    Task SetRepeatModeAsync(RepeatMode mode);

    #endregion

    #region Queue Management

    /// <summary>
    ///     Adds a song to the end of the playback queue.
    /// </summary>
    Task AddToQueueAsync(Song song);

    /// <summary>
    ///     Adds a collection of songs to the end of the playback queue.
    /// </summary>
    Task AddRangeToQueueAsync(IEnumerable<Song> songs);

    /// <summary>
    ///     Inserts a song immediately after the current track in the queue.
    /// </summary>
    Task PlayNextAsync(Song song);

    /// <summary>
    ///     Removes a specific song from the playback queue. If the current song is removed, plays the next one.
    /// </summary>
    Task RemoveFromQueueAsync(Song song);

    /// <summary>
    ///     Plays a specific song from the current queue by its index in the original (non-shuffled) queue.
    /// </summary>
    /// <param name="originalQueueIndex">The 0-based index of the song in the original playback queue.</param>
    Task PlayQueueItemAsync(int originalQueueIndex);

    /// <summary>
    ///     Clears all songs from the playback queue and stops playback.
    /// </summary>
    Task ClearQueueAsync();

    /// <summary>
    ///     Saves the current playback state (queue, track, position) to persistent storage.
    /// </summary>
    Task SavePlaybackStateAsync();

    #endregion
}