using Nagi.Core.Models;
using Nagi.Core.Services.Data;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Nagi.Core.Services.Abstractions;

/// <summary>
/// Defines the contract for a service that manages music playback, queue, and state.
/// </summary>
public interface IMusicPlaybackService : IDisposable {
    /// <summary>
    /// Gets the currently playing song, or null if nothing is playing.
    /// </summary>
    Song? CurrentTrack { get; }

    /// <summary>
    /// Gets the unique database ID for the current listening session.
    /// This is used to link playback events to a specific entry in the ListenHistory table.
    /// </summary>
    long? CurrentListenHistoryId { get; }

    /// <summary>
    /// Gets a value indicating whether the audio player is currently playing.
    /// </summary>
    bool IsPlaying { get; }

    /// <summary>
    /// Gets the current playback position of the track.
    /// </summary>
    TimeSpan CurrentPosition { get; }

    /// <summary>
    /// Gets the total duration of the current track.
    /// </summary>
    TimeSpan Duration { get; }

    /// <summary>
    /// Gets the current volume level (0.0 to 1.0).
    /// </summary>
    double Volume { get; }

    /// <summary>
    /// Gets a value indicating whether the player is muted.
    /// </summary>
    bool IsMuted { get; }

    /// <summary>
    /// Gets a read-only list of songs in the original, unshuffled playback queue.
    /// </summary>
    IReadOnlyList<Song> PlaybackQueue { get; }

    /// <summary>
    /// Gets a read-only list of songs in the shuffled playback queue.
    /// </summary>
    IReadOnlyList<Song> ShuffledQueue { get; }

    /// <summary>
    /// Gets the index of the current track in the original PlaybackQueue.
    /// </summary>
    int CurrentQueueIndex { get; }

    /// <summary>
    /// Gets a value indicating whether shuffle mode is enabled.
    /// </summary>
    bool IsShuffleEnabled { get; }

    /// <summary>
    /// Gets the current repeat mode (Off, RepeatAll, RepeatOne).
    /// </summary>
    RepeatMode CurrentRepeatMode { get; }

    /// <summary>
    /// Gets a value indicating whether the service is in the process of changing tracks.
    /// </summary>
    bool IsTransitioningTrack { get; }

    /// <summary>
    /// Fires when the current track changes or playback stops.
    /// </summary>
    event Action? TrackChanged;

    /// <summary>
    /// Fires when the playback state changes (e.g., playing, paused).
    /// </summary>
    event Action? PlaybackStateChanged;

    /// <summary>
    /// Fires when the volume or mute state changes.
    /// </summary>
    event Action? VolumeStateChanged;

    /// <summary>
    /// Fires when the shuffle mode is enabled or disabled.
    /// </summary>
    event Action? ShuffleModeChanged;

    /// <summary>
    /// Fires when the repeat mode changes.
    /// </summary>
    event Action? RepeatModeChanged;

    /// <summary>
    /// Fires when the playback queue is modified (e.g., songs added, removed, reordered).
    /// </summary>
    event Action? QueueChanged;

    /// <summary>
    /// Fires as the playback position of the current track changes.
    /// </summary>
    event Action? PositionChanged;

    /// <summary>
    /// Initializes the service, loading settings and saved playback state.
    /// </summary>
    Task InitializeAsync();

    /// <summary>
    /// Plays a single song, replacing the current queue.
    /// </summary>
    Task PlayAsync(Song song);

    /// <summary>
    /// Plays a list of songs, replacing the current queue.
    /// </summary>
    /// <param name="songs">The collection of songs to play.</param>
    /// <param name="startIndex">The index in the collection to start playing from.</param>
    /// <param name="startShuffled">If true, shuffle will be enabled before playing.</param>
    Task PlayAsync(IEnumerable<Song> songs, int startIndex = 0, bool startShuffled = false);

    /// <summary>
    /// Toggles between playing and pausing the current track.
    /// </summary>
    Task PlayPauseAsync();

    /// <summary>
    /// Stops playback and clears the current track.
    /// </summary>
    Task StopAsync();

    /// <summary>
    /// Skips to the next track in the queue.
    /// </summary>
    Task NextAsync();

    /// <summary>
    /// Goes to the previous track in the queue or restarts the current track.
    /// </summary>
    Task PreviousAsync();

    /// <summary>
    /// Seeks to a specific position in the current track.
    /// </summary>
    Task SeekAsync(TimeSpan position);

    Task PlayAlbumAsync(Guid albumId);
    Task PlayArtistAsync(Guid artistId);
    Task PlayFolderAsync(Guid folderId);
    Task PlayPlaylistAsync(Guid playlistId);
    Task PlayGenreAsync(Guid genreId);

    /// <summary>
    /// Sets the player volume.
    /// </summary>
    /// <param name="volume">The new volume level (0.0 to 1.0).</param>
    Task SetVolumeAsync(double volume);

    /// <summary>
    /// Toggles the mute state of the player.
    /// </summary>
    Task ToggleMuteAsync();

    /// <summary>
    /// Enables or disables shuffle mode.
    /// </summary>
    Task SetShuffleAsync(bool enable);

    /// <summary>
    /// Cycles through the available repeat modes.
    /// </summary>
    Task SetRepeatModeAsync(RepeatMode mode);

    Task AddToQueueAsync(Song song);
    Task AddRangeToQueueAsync(IEnumerable<Song> songs);
    Task PlayNextAsync(Song song);
    Task RemoveFromQueueAsync(Song song);
    Task PlayQueueItemAsync(int originalQueueIndex);
    Task ClearQueueAsync();

    /// <summary>
    /// Saves the current playback state (queue, track, position) to persistent storage.
    /// </summary>
    Task SavePlaybackStateAsync();
}