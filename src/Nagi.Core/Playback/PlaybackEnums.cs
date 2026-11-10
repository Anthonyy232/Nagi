using Nagi.Core.Library;

namespace Nagi.Core.Playback;

public enum PlaybackEndReason
{
    Finished = 0,

    /// <summary>
    ///     Another track was chosen, or this one was removed from the queue, before it ended.
    /// </summary>
    Skipped = 1,

    /// <summary>
    ///     Playback stopped or the app closed before the track ended.
    /// </summary>
    PausedAndAbandoned = 2
}

public enum PlaybackContextType
{
    Album = 0,

    Artist = 1,

    Playlist = 2,

    SmartPlaylist = 3,

    Folder = 4,

    Genre = 5,

    Search = 6,

    /// <summary>
    ///     Tracks played from the main library or ad-hoc queue.
    /// </summary>
    Library = 7,

    /// <summary>
    ///     Played as a single file without library context.
    /// </summary>
    Transient = 8
}

public enum PlaybackStopMode
{
    Off,
    AfterTrack,
    EndOfQueue,
    Timer
}

public record PlaybackContext(PlaybackContextType Type, Guid? ContextId)
{
    public static PlaybackContext Of(SongScope scope) => scope switch
    {
        SongScope.Album album => new(PlaybackContextType.Album, album.AlbumId),
        SongScope.Artist artist => new(PlaybackContextType.Artist, artist.ArtistId),
        SongScope.Genre genre => new(PlaybackContextType.Genre, genre.GenreId),
        SongScope.Playlist playlist => new(PlaybackContextType.Playlist, playlist.PlaylistId),
        SongScope.Folder folder => new(PlaybackContextType.Folder, folder.RootFolderId),
        SongScope.Directory directory => new(PlaybackContextType.Folder, directory.RootFolderId),
        SongScope.DirectoryTree tree => new(PlaybackContextType.Folder, tree.RootFolderId),
        _ => new(PlaybackContextType.Library, null)
    };
}

/// <summary>
///     The playback state shown by the operating system's media controls.
/// </summary>
public enum SystemMediaPlaybackStatus
{
    Closed,
    Changing,
    Stopped,
    Playing,
    Paused
}
