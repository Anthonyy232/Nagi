using Nagi.Core.OnlineProviders.Pipelines;

namespace Nagi.Core.OnlineProviders;

[Flags]
public enum ProviderCapabilities
{
    None = 0,
    ArtistImage = 1,
    ArtistBiography = 2,
    AlbumCover = 4,
    Lyrics = 8,

    /// <summary>Finds the MusicBrainz IDs that other providers look artists and albums up by.</summary>
    IdResolver = 16
}

/// <summary>An online source the user can turn on and rank in settings.</summary>
/// <param name="DisplayName">The provider's name, which isn't translated.</param>
public sealed record OnlineProvider(string Id, string DisplayName, ProviderCapabilities Capabilities, ProviderPolicy Policy)
{
    public bool Can(ProviderCapabilities capabilities) => (Capabilities & capabilities) != 0;
}

/// <summary>
///     Every online source Nagi asks, what it can provide and how fast it may be called. The request pipelines are
///     built from this table, so a provider can't be added without a policy.
/// </summary>
public static class ProviderCatalog
{
    // MusicBrainz allows 1 request a second per IP from a client named in its User-Agent, and answers 503 above that.
    public static readonly OnlineProvider MusicBrainz = Provider(ServiceProviderIds.MusicBrainz, "MusicBrainz",
        ProviderCapabilities.IdResolver,
        new ChannelPolicy { PermitsPerWindow = 1, MaxConcurrent = 1, BaseRetryDelay = TimeSpan.FromSeconds(1) });

    // Wikimedia has no fixed limit for reads but asks clients to keep requests serial and modest.
    public static readonly OnlineProvider Wikipedia = Provider(ServiceProviderIds.Wikipedia, "Wikipedia",
        ProviderCapabilities.ArtistBiography,
        new ChannelPolicy { PermitsPerWindow = 5, MaxConcurrent = 2 });

    // The Cover Art Archive is served from the Internet Archive with no published limit.
    public static readonly OnlineProvider CoverArtArchive = Provider(ServiceProviderIds.CoverArtArchive, "Cover Art Archive",
        ProviderCapabilities.AlbumCover,
        new ChannelPolicy { PermitsPerWindow = 5, MaxConcurrent = 4 });

    // Fanart.tv is effectively unlimited for normal use; 429s are rare and their Retry-After is honored.
    public static readonly OnlineProvider FanartTv = Provider(ServiceProviderIds.FanartTv, "Fanart.tv",
        ProviderCapabilities.ArtistImage,
        new ChannelPolicy { PermitsPerWindow = 10, MaxConcurrent = 8 });

    // TheAudioDB's free tier allows 30 requests a minute. Past that the key is locked out for a full minute, so
    // short retry delays only waste calls: wait the full minute, once.
    public static readonly OnlineProvider TheAudioDb = Provider(ServiceProviderIds.TheAudioDb, "TheAudioDB",
        ProviderCapabilities.ArtistImage | ProviderCapabilities.ArtistBiography,
        new ChannelPolicy
        {
            PermitsPerWindow = 1,
            Window = TimeSpan.FromSeconds(2),
            MaxConcurrent = 2,
            MaxRetries = 1,
            BaseRetryDelay = TimeSpan.FromSeconds(60),
            MaxRetryDelay = TimeSpan.FromSeconds(75)
        });

    // Deezer allows 50 requests per 5 seconds per IP; past that it answers 200 with a quota error.
    public static readonly OnlineProvider Deezer = Provider(ServiceProviderIds.Deezer, "Deezer",
        ProviderCapabilities.ArtistImage | ProviderCapabilities.AlbumCover,
        new ChannelPolicy { PermitsPerWindow = 8, MaxConcurrent = 4 });

    // The iTunes Search API allows about 20 requests a minute per IP. Past that it answers 403 for a while, which
    // leaves the album retryable on a later run.
    public static readonly OnlineProvider AppleMusic = Provider(ServiceProviderIds.AppleMusic, "Apple Music",
        ProviderCapabilities.AlbumCover,
        new ChannelPolicy { PermitsPerWindow = 1, Window = TimeSpan.FromSeconds(3), MaxConcurrent = 1 });

    // Metadata, sign-in and scrobbling share this policy, so an auth failure in one pauses the others. Last.fm's
    // limits are undisclosed; 5 requests a second per IP is the long-standing convention.
    public static readonly OnlineProvider LastFm = Provider(ServiceProviderIds.LastFm, "Last.fm",
        ProviderCapabilities.ArtistImage | ProviderCapabilities.ArtistBiography | ProviderCapabilities.AlbumCover,
        new ChannelPolicy { PermitsPerWindow = 5, MaxConcurrent = 4, BaseRetryDelay = TimeSpan.FromSeconds(5) });

    // LRCLIB documents no rate limit.
    public static readonly OnlineProvider LrcLib = Provider(ServiceProviderIds.LrcLib, "LRCLIB",
        ProviderCapabilities.Lyrics,
        new ChannelPolicy { PermitsPerWindow = 20, MaxConcurrent = 8 });

    // NetEase's API is unofficial and rate-limits aggressively, so back off quickly.
    public static readonly OnlineProvider NetEase = Provider(ServiceProviderIds.NetEase, "NetEase",
        ProviderCapabilities.Lyrics,
        new ChannelPolicy { PermitsPerWindow = 2, MaxConcurrent = 2, BaseRetryDelay = TimeSpan.FromSeconds(3) });

    // ListenBrainz advertises a dynamic limit in X-RateLimit-* headers, and the pipeline honors its reset delay.
    public static readonly ProviderPolicy ListenBrainz = Policy(ServiceProviderIds.ListenBrainz,
        new ChannelPolicy { PermitsPerWindow = 5, MaxConcurrent = 4 });

    // Nagi's own server, which hands out the API keys of the other providers.
    public static readonly ProviderPolicy NagiApi = Policy("nagi-api",
        new ChannelPolicy { PermitsPerWindow = 5, MaxConcurrent = 4 });

    // Artist and album images come from many CDNs, and the source isn't always knowable from the URL.
    public static readonly ProviderPolicy ImageDownload = Policy("image-download",
        new ChannelPolicy { PermitsPerWindow = 10, MaxConcurrent = 4 });

    /// <summary>Every provider, in the default priority order.</summary>
    public static readonly IReadOnlyList<OnlineProvider> All =
    [
        MusicBrainz, Wikipedia, CoverArtArchive, FanartTv, TheAudioDb, Deezer, AppleMusic, LastFm, LrcLib, NetEase
    ];

    internal static IEnumerable<ProviderPolicy> Policies =>
        All.Select(provider => provider.Policy).Append(ListenBrainz).Append(NagiApi).Append(ImageDownload);

    public static bool Can(string providerId, ProviderCapabilities capabilities) =>
        All.Any(provider => provider.Id == providerId && provider.Can(capabilities));

    public static OnlineProvider Get(string providerId) => All.First(provider => provider.Id == providerId);

    private static OnlineProvider Provider(string id, string displayName, ProviderCapabilities capabilities,
        ChannelPolicy channel) =>
        new(id, displayName, capabilities, Policy(id, channel));

    private static ProviderPolicy Policy(string id, ChannelPolicy channel) => new() { ProviderId = id, Channel = channel };
}
