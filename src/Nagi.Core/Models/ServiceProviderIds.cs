namespace Nagi.Core.Models;

public static class ServiceProviderIds
{
    #region Lyrics Providers

    public const string LrcLib = "lrclib";

    public const string NetEase = "netease";

    #endregion

    #region Metadata Providers

    /// <summary>MusicBrainz - Open music encyclopedia, provides artist/release IDs.</summary>
    public const string MusicBrainz = "musicbrainz";

    /// <summary>TheAudioDB - High-quality artist images and biographies. Requires MusicBrainz ID.</summary>
    public const string TheAudioDb = "theaudiodb";

    /// <summary>Fanart.tv - Fan-contributed artist artwork. Requires MusicBrainz ID.</summary>
    public const string FanartTv = "fanarttv";

    /// <summary>Last.fm - Fallback for artist images and biographies.</summary>
    public const string LastFm = "lastfm";

    /// <summary>Deezer - Artist images found by name, no ID or key needed.</summary>
    public const string Deezer = "deezer";

    /// <summary>Last.fm API Secret name for retrieval.</summary>
    public const string LastFmSecret = "lastfm-secret";

    public const string ListenBrainz = "listenbrainz";

    #endregion

    #region Internal Providers

    /// <summary>Nagi's own API server (key bootstrap). Not a third-party provider.</summary>
    public const string NagiApi = "nagi-api";

    /// <summary>
    ///     Generic bucket for cover-art / artist-image downloads from third-party CDNs.
    ///     Used when the originating provider is not knowable at download time.
    /// </summary>
    public const string ImageDownload = "image-download";

    #endregion
}
