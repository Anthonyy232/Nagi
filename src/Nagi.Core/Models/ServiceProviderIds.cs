namespace Nagi.Core.Models;

/// <summary>
///     Constants for service provider identifiers used throughout the application.
///     Centralizes provider IDs to prevent typos and enable compile-time checking.
/// </summary>
public static class ServiceProviderIds
{
    #region Lyrics Providers

    /// <summary>LRCLIB</summary>
    public const string LrcLib = "lrclib";

    /// <summary>NetEase</summary>
    public const string NetEase = "netease";

    #endregion

    #region Metadata Providers

    /// <summary>MusicBrainz - Open music encyclopedia, provides artist/release IDs.</summary>
    public const string MusicBrainz = "musicbrainz";

    /// <summary>TheAudioDB - High-quality artist images and biographies. Requires MusicBrainz ID.</summary>
    public const string TheAudioDb = "theaudiodb";

    /// <summary>Fanart.tv - Fan-contributed artist artwork. Requires MusicBrainz ID.</summary>
    public const string FanartTv = "fanarttv";

    /// <summary>Spotify - Artist images via Spotify Web API.</summary>
    public const string Spotify = "spotify";

    /// <summary>Last.fm - Fallback for artist images and biographies.</summary>
    public const string LastFm = "lastfm";

    /// <summary>Last.fm API Secret name for retrieval.</summary>
    public const string LastFmSecret = "lastfm-secret";

    #endregion
}
