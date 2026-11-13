using Nagi.Core.OnlineProviders;
using Nagi.Core.Playback;

namespace Nagi.Core.Settings;

/// <summary>
///     The settings Core's services read.
/// </summary>
public static class CoreSettings
{
    /// <summary>From 0.0 to 1.0.</summary>
    public static readonly Setting<double> Volume = Setting.Value("AppVolume", 0.5, volume => Math.Clamp(volume, 0.0, 1.0));
    public static readonly Setting<bool> IsMuted = Setting.Value("AppMuteState", false);
    public static readonly Setting<bool> IsShuffleEnabled = Setting.Value("MusicShuffleState", false);
    public static readonly Setting<RepeatMode> RepeatMode = Setting.EnumName("MusicRepeatMode", Playback.RepeatMode.Off);
    public static readonly Setting<bool> RestorePlaybackState = Setting.Value("RestorePlaybackStateEnabled", true);

    /// <summary>Null until the equalizer is first set up for the audio player's bands.</summary>
    public static readonly Setting<EqualizerSettings?> Equalizer =
        Setting.Json<EqualizerSettings?>("EqualizerSettings", () => null);

    public static readonly Setting<bool> VolumeNormalization = Setting.Value("VolumeNormalizationEnabled", false);
    public static readonly Setting<bool> WasapiExclusiveMode = Setting.Value("WasapiExclusiveModeEnabled", false);

    /// <summary>Empty for the system's default output.</summary>
    public static readonly Setting<string> AudioOutputDeviceId = Setting.Value("AudioOutputDeviceId", string.Empty);

    public static readonly Setting<bool> FadeOnPlayPause = Setting.Value("FadeOnPlayPauseEnabled", false);
    public static readonly Setting<int> FadeInDurationMs = Setting.Value("FadeInDurationMs", 200);
    public static readonly Setting<int> FadeOutDurationMs = Setting.Value("FadeOutDurationMs", 150);

    public static readonly Setting<bool> FetchOnlineMetadata = Setting.Value("FetchOnlineMetadataEnabled", false);
    public static readonly Setting<bool> FetchOnlineLyrics = Setting.Value("FetchOnlineLyricsEnabled", false);
    public static readonly Setting<bool> LyricsRomanization = Setting.Value("LyricsRomanizationEnabled", false);

    /// <summary>Whether leading English articles ("the", "a", "an") are ignored when sorting by name.</summary>
    public static readonly Setting<bool> IgnoreLeadingArticlesOnSort = Setting.Value("IgnoreLeadingArticlesOnSort", true);

    /// <summary>The characters that separate several artists in one tag. Empty to not split.</summary>
    public static readonly Setting<string> ArtistSplitCharacters = Setting.Value("ArtistSplitCharacters", string.Empty);

    /// <summary>The characters that separate several genres in one tag. Empty to not split.</summary>
    public static readonly Setting<string> GenreSplitCharacters = Setting.Value("GenreSplitCharacters", string.Empty);

    /// <summary>Every lyrics provider in priority order, enabled or not.</summary>
    public static readonly Setting<List<ServiceProviderSetting>> LyricsProviders =
        ServiceProviders("LyricsServiceProviders", ProviderCatalog.All.Where(p => p.Can(ProviderCapabilities.Lyrics)));

    /// <summary>Every metadata provider in priority order, enabled or not.</summary>
    public static readonly Setting<List<ServiceProviderSetting>> MetadataProviders =
        ServiceProviders("MetadataServiceProviders", ProviderCatalog.All.Where(p => !p.Can(ProviderCapabilities.Lyrics)));

    public static readonly Setting<bool> DiscordRichPresence = Setting.Value("DiscordRichPresenceEnabled", false);

    public static readonly Setting<bool> LastFmScrobbling = Setting.Value("LastFmScrobblingEnabled", false);
    public static readonly Setting<bool> LastFmNowPlaying = Setting.Value("LastFmNowPlayingEnabled", false);

    /// <summary>The token of a Last.fm sign-in waiting for the user to approve it in the browser, or null.</summary>
    public static readonly Setting<string?> LastFmAuthToken = Setting.Value<string?>("LastFmAuthToken", null);

    public static readonly Setting<bool> ListenBrainzScrobbling = Setting.Value("ListenBrainzScrobblingEnabled", false);
    public static readonly Setting<bool> ListenBrainzNowPlaying = Setting.Value("ListenBrainzNowPlayingEnabled", false);

    /// <summary>Null for the default server (api.listenbrainz.org).</summary>
    public static readonly Setting<string?> ListenBrainzServerUrl = Setting.Value<string?>("ListenBrainzServerUrl", null);

    /// <summary>
    ///     When the ListenBrainz account was connected, in UTC. The offline queue ignores listens from before it.
    ///     Null while no account is connected.
    /// </summary>
    public static readonly Setting<DateTime?> ListenBrainzEnabledSinceUtc = new("ListenBrainzEnabledSinceUtcTicks",
        () => null,
        stored => stored is long ticks and not 0 ? new DateTime(ticks, DateTimeKind.Utc) : null,
        timestamp => timestamp?.ToUniversalTime().Ticks ?? 0L);

    // Older versions also saved each provider's name, category and position, which are ignored now; they wrote the
    // list in priority order too.
    private static Setting<List<ServiceProviderSetting>> ServiceProviders(string key, IEnumerable<OnlineProvider> providers)
    {
        var defaultOrder = providers.Select(provider => provider.Id).ToList();
        List<ServiceProviderSetting> Defaults() => defaultOrder.Select(id => new ServiceProviderSetting(id)).ToList();

        return Setting.Json(key, Defaults, saved =>
        {
            var ranked = saved.DistinctBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
                .Join(defaultOrder, p => p.Id, id => id, (configuration, id) => configuration with { Id = id },
                    StringComparer.OrdinalIgnoreCase)
                .ToList();

            // A source added by an update goes below every source ranked above it by default, wherever the user
            // moved those, so it never outranks a source the user already preferred.
            for (var i = 0; i < defaultOrder.Count; i++)
            {
                if (ranked.Any(p => p.Id == defaultOrder[i])) continue;
                var higherRanked = defaultOrder.Take(i).ToHashSet();
                var insertAt = ranked.FindLastIndex(p => higherRanked.Contains(p.Id)) + 1;
                ranked.Insert(insertAt, new ServiceProviderSetting(defaultOrder[i]));
            }

            return ranked;
        });
    }
}
