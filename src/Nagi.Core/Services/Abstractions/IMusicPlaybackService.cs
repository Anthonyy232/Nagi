using Nagi.Core.Models;
using Nagi.Core.Services.Data;

namespace Nagi.Core.Services.Abstractions;

/// <summary>
///     Defines the contract for a service that manages music playback, queue, and state.
/// </summary>
public interface IMusicPlaybackService : IDisposable
{
    /// <summary>
    ///     Gets the currently playing song, or null if nothing is playing.
    /// </summary>
    Song? CurrentTrack { get; }

    /// <summary>
    ///     Gets the unique database ID for the current listening session.
    ///     This is used to link playback events to a specific entry in the ListenHistory table.
    /// </summary>
    long? CurrentListenHistoryId { get; }

    /// <summary>
    ///     Gets a value indicating whether the audio player is currently playing.
    /// </summary>
    bool IsPlaying { get; }

    /// <summary>
    ///     Gets the current playback position of the track.
    /// </summary>
    TimeSpan CurrentPosition { get; }

    /// <summary>
    ///     Gets the total duration of the current track.
    /// </summary>
    TimeSpan Duration { get; }

    /// <summary>
    ///     Gets the current volume level (0.0 to 1.0).
    /// </summary>
    double Volume { get; }

    /// <summary>
    ///     Gets a value indicating whether the player is muted.
    /// </summary>
    bool IsMuted { get; }

    /// <summary>
    ///     Gets a read-only list of songs in the original, unshuffled playback queue.
    /// </summary>
    IReadOnlyList<Song> PlaybackQueue { get; }

    /// <summary>
    ///     Gets a read-only list of songs in the shuffled playback queue.
    /// </summary>
    IReadOnlyList<Song> ShuffledQueue { get; }

    /// <summary>
    ///     Gets the index of the current track in the original PlaybackQueue.
    /// </summary>
    int CurrentQueueIndex { get; }

    /// <summary>
    ///     Gets a value indicating whether shuffle mode is enabled.
    /// </summary>
    bool IsShuffleEnabled { get; }

    /// <summary>
    ///     Gets the current repeat mode (Off, RepeatAll, RepeatOne).
    /// </summary>
    RepeatMode CurrentRepeatMode { get; }

    /// <summary>
    ///     Gets a value indicating whether the service is in the process of changing tracks.
    /// </summary>
    bool IsTransitioningTrack { get; }

    /// <summary>
    ///     Occurs when the current track changes or playback stops.
    /// </summary>
    event Action? TrackChanged;

    /// <summary>
    ///     Occurs when the playback state changes (e.g., playing, paused).
    /// </summary>
    event Action? PlaybackStateChanged;

    /// <summary>
    ///     Occurs when the volume or mute state changes.
    /// </summary>
    event Action? VolumeStateChanged;

    /// <summary>
    ///     Occurs when the shuffle mode is enabled or disabled.
    /// </summary>
    event Action? ShuffleModeChanged;

    /// <summary>
    ///     Occurs when the repeat mode changes.
    /// </summary>
    event Action? RepeatModeChanged;

    /// <summary>
    ///     Occurs when the playback queue is modified (e.g., songs added, removed, reordered).
    /// </summary>
    event Action? QueueChanged;

    /// <summary>
    ///     Occurs as the playback position of the current track changes.
    /// </summary>
    event Action? PositionChanged;

    /// <summary>
    ///     Occurs when the duration of the current track becomes known.
    /// </summary>
    event Action? DurationChanged;

    /// <summary>
    ///     Initializes the service, loading settings and saved playback state.
    /// </summary>
    Task InitializeAsync(bool restoreLastSession = true);

    /// <summary>
    ///     Plays a single song, replacing the current queue.
    /// </summary>
    /// <param name="song">The song to play.</param>
    Task PlayAsync(Song song);

    /// <summary>
    ///     Plays a list of songs, replacing the current queue.
    /// </summary>
    /// <param name="songs">The collection of songs to play.</param>
    /// <param name="startIndex">The index in the collection to start playing from.</param>
    /// <param name="startShuffled">If true, shuffle will be enabled before playing.</param>
    Task PlayAsync(IEnumerable<Song> songs, int startIndex = 0, bool startShuffled = false);

    /// <summary>
    ///     Toggles between playing and pausing the current track.
    /// </summary>
    Task PlayPauseAsync();

    /// <summary>
    ///     Stops playback and clears the current track.
    /// </summary>
    Task StopAsync();

    /// <summary>
    ///     Skips to the next track in the queue.
    /// </summary>
    Task NextAsync();

    /// <summary>
    ///     Goes to the previous track in the queue or restarts the current track.
    /// </summary>
    Task PreviousAsync();

    /// <summary>
    ///     Seeks to a specific position in the current track.
    /// </summary>
    /// <param name="position">The position to seek to.</param>
    Task SeekAsync(TimeSpan position);

    /// <summary>
    ///     Creates a new queue from all songs by the specified album and starts playback.
    /// </summary>
    /// <param name="albumId">The ID of the album to play.</param>
    Task PlayAlbumAsync(Guid albumId);

    /// <summary>
    ///     Creates a new queue from all songs by the specified artist and starts playback.
    /// </summary>
    /// <param name="artistId">The ID of the artist to play.</param>
    Task PlayArtistAsync(Guid artistId);

    /// <summary>
    ///     Creates a new queue from all songs in the specified folder and starts playback.
    /// </summary>
    /// <param name="folderId">The ID of the folder to play.</param>
    Task PlayFolderAsync(Guid folderId);

    /// <summary>
    ///     Creates a new queue from the specified playlist and starts playback.
    /// </summary>
    /// <param name="playlistId">The ID of the playlist to play.</param>
    Task PlayPlaylistAsync(Guid playlistId);

    /// <summary>
    ///     Creates a new queue from all songs of the specified genre and starts playback.
    /// </summary>
    /// <param name="genreId">The ID of the genre to play.</param>
    Task PlayGenreAsync(Guid genreId);

    /// <summary>
    ///     Sets the player volume.
    /// </summary>
    /// <param name="volume">The new volume level (0.0 to 1.0).</param>
    Task SetVolumeAsync(double volume);

    /// <summary>
    ///     Toggles the mute state of the player.
    /// </summary>
    Task ToggleMuteAsync();

    /// <summary>
    ///     Enables or disables shuffle mode.
    /// </summary>
    /// <param name="enable">True to enable shuffle, false to disable.</param>
    Task SetShuffleAsync(bool enable);

    /// <summary>
    ///     Cycles through the available repeat modes.
    /// </summary>
    /// <param name="mode">The desired repeat mode.</param>
    Task SetRepeatModeAsync(RepeatMode mode);

    /// <summary>
    ///     Adds a song to the end of the current queue.
    /// </summary>
    /// <param name="song">The song to add.</param>
    Task AddToQueueAsync(Song song);

    /// <summary>
    ///     Adds a collection of songs to the end of the current queue.
    /// </summary>
    /// <param name="songs">The songs to add.</param>
    Task AddRangeToQueueAsync(IEnumerable<Song> songs);

    /// <summary>
    ///     Inserts a song into the queue immediately after the current track.
    /// </summary>
    /// <param name="song">The song to play next.</param>
    Task PlayNextAsync(Song song);

    /// <summary>
    ///     Plays a single audio file directly from its path, without it needing to be
    ///     in the main playback queue or library.
    /// </summary>
    /// <param name="filePath">The absolute path to the audio file.</param>
    Task PlayTransientFileAsync(string filePath);

    /// <summary>
    ///     Removes a song from the queue.
    /// </summary>
    /// <param name="song">The song to remove.</param>
    Task RemoveFromQueueAsync(Song song);

    /// <summary>
    ///     Jumps to and plays a specific item in the queue.
    /// </summary>
    /// <param name="originalQueueIndex">The index of the song in the original, unshuffled queue.</param>
    Task PlayQueueItemAsync(int originalQueueIndex);

    /// <summary>
    ///     Stops playback and clears the entire queue.
    /// </summary>
    Task ClearQueueAsync();

    /// <summary>
    ///     Saves the current playback state (queue, track, position) to persistent storage.
    /// </summary>
    Task SavePlaybackStateAsync();
}