using Nagi.Core.Models;
using Nagi.Core.Services.Data;

namespace Nagi.Core.Services.Abstractions;

/// <summary>
///     Defines a service for managing application-wide, non-UI settings.
///     This interface is safe to use in the Core project.
/// </summary>
public interface ISettingsService
{
    event Action? LastFmSettingsChanged;

    event Action? ListenBrainzSettingsChanged;

    event Action<bool>? DiscordRichPresenceSettingChanged;

    event Action<bool>? VolumeNormalizationEnabledChanged;

    event Action<bool>? FadeOnPlayPauseEnabledChanged;

    event Action<int>? FadeInDurationChanged;

    event Action<int>? FadeOutDurationChanged;

    event Action<bool>? FetchOnlineMetadataEnabledChanged;

    event Action<bool>? FetchOnlineLyricsEnabledChanged;

    event Action<bool>? LyricsRomanizationEnabledChanged;

    /// <summary>
    ///     Gets the initial volume level for the media player.
    /// </summary>
    /// <returns>A volume level between 0.0 and 1.0.</returns>
    Task<double> GetInitialVolumeAsync();

    /// <summary>
    ///     Saves the current volume level.
    /// </summary>
    /// <param name="volume">The volume level to save, clamped between 0.0 and 1.0.</param>
    Task SaveVolumeAsync(double volume);

    Task<bool> GetInitialMuteStateAsync();

    Task SaveMuteStateAsync(bool isMuted);

    Task<bool> GetInitialShuffleStateAsync();

    Task SaveShuffleStateAsync(bool isEnabled);

    Task<RepeatMode> GetInitialRepeatModeAsync();

    Task SaveRepeatModeAsync(RepeatMode mode);

    Task<bool> GetRestorePlaybackStateEnabledAsync();

    Task SetRestorePlaybackStateEnabledAsync(bool isEnabled);

    Task<bool> GetFetchOnlineMetadataEnabledAsync();

    Task SetFetchOnlineMetadataEnabledAsync(bool isEnabled);

    Task<bool> GetDiscordRichPresenceEnabledAsync();

    Task SetDiscordRichPresenceEnabledAsync(bool isEnabled);

    /// <summary>
    ///     Saves the current playback state, including the queue and current track.
    /// </summary>
    /// <param name="state">The playback state to save. If null, the saved state is cleared.</param>
    Task SavePlaybackStateAsync(PlaybackState? state);

    /// <summary>
    ///     Retrieves the last saved playback state.
    /// </summary>
    /// <returns>The saved <see cref="PlaybackState" />, or null if none exists.</returns>
    Task<PlaybackState?> GetPlaybackStateAsync();

    Task ClearPlaybackStateAsync();

    Task<bool> GetFetchOnlineLyricsEnabledAsync();

    Task SetFetchOnlineLyricsEnabledAsync(bool isEnabled);

    Task<bool> GetLyricsRomanizationEnabledAsync();

    Task SetLyricsRomanizationEnabledAsync(bool isEnabled);

    Task<bool> GetLastFmScrobblingEnabledAsync();

    Task SetLastFmScrobblingEnabledAsync(bool isEnabled);

    Task<bool> GetLastFmNowPlayingEnabledAsync();

    Task SetLastFmNowPlayingEnabledAsync(bool isEnabled);

    /// <summary>
    ///     Gets the saved Last.fm credentials from secure storage.
    /// </summary>
    /// <returns>A tuple containing the username and session key, or null if not present.</returns>
    Task<(string? Username, string? SessionKey)?> GetLastFmCredentialsAsync();

    /// <summary>
    ///     Saves the Last.fm credentials securely.
    /// </summary>
    /// <param name="username">The username to save.</param>
    /// <param name="sessionKey">The session key to save.</param>
    Task SaveLastFmCredentialsAsync(string username, string sessionKey);

    Task ClearLastFmCredentialsAsync();

    /// <summary>
    ///     Saves the temporary authentication token received from Last.fm.
    /// </summary>
    /// <param name="token">The token to save, or null to clear it.</param>
    Task SaveLastFmAuthTokenAsync(string? token);

    Task<string?> GetLastFmAuthTokenAsync();

    Task<bool> GetListenBrainzScrobblingEnabledAsync();

    Task SetListenBrainzScrobblingEnabledAsync(bool isEnabled);

    Task<bool> GetListenBrainzNowPlayingEnabledAsync();

    Task SetListenBrainzNowPlayingEnabledAsync(bool isEnabled);

    /// <summary>Gets the saved ListenBrainz user token from secure storage, or null.</summary>
    Task<string?> GetListenBrainzUserTokenAsync();

    /// <summary>Saves the ListenBrainz user token to secure storage.</summary>
    Task SaveListenBrainzUserTokenAsync(string token);

    Task ClearListenBrainzUserTokenAsync();

    /// <summary>Gets the configured ListenBrainz server URL, or null for the default (api.listenbrainz.org).</summary>
    Task<string?> GetListenBrainzServerUrlAsync();

    /// <summary>Sets the ListenBrainz server URL. Pass null to reset to default.</summary>
    Task SetListenBrainzServerUrlAsync(string? url);

    /// <summary>
    ///     Gets the UTC timestamp at which ListenBrainz submission was first enabled. Listens with
    ///     <c>ListenTimestampUtc</c> before this cutoff are ignored by the offline queue.
    /// </summary>
    Task<DateTime?> GetListenBrainzEnabledSinceUtcAsync();

    /// <summary>Sets the ListenBrainz enabled-since cutoff. Pass null to clear.</summary>
    Task SetListenBrainzEnabledSinceUtcAsync(DateTime? timestamp);

    Task<string> GetArtistSplitCharactersAsync();

    Task SetArtistSplitCharactersAsync(string characters);

    Task<string> GetGenreSplitCharactersAsync();

    Task SetGenreSplitCharactersAsync(string characters);

    /// <summary>
    ///     Retrieves the last saved equalizer settings.
    /// </summary>
    /// <returns>The saved <see cref="EqualizerSettings" />, or null if none exist.</returns>
    Task<EqualizerSettings?> GetEqualizerSettingsAsync();

    Task SetEqualizerSettingsAsync(EqualizerSettings settings);

    Task<bool> GetVolumeNormalizationEnabledAsync();

    Task SetVolumeNormalizationEnabledAsync(bool isEnabled);

    Task<bool> GetWasapiExclusiveModeEnabledAsync();

    Task SetWasapiExclusiveModeEnabledAsync(bool isEnabled);

    Task<bool> GetFadeOnPlayPauseEnabledAsync();

    Task SetFadeOnPlayPauseEnabledAsync(bool isEnabled);

    Task<int> GetFadeInDurationMsAsync();

    Task SetFadeInDurationMsAsync(int durationMs);

    Task<int> GetFadeOutDurationMsAsync();

    Task SetFadeOutDurationMsAsync(int durationMs);

    /// <summary>
    ///     Gets whether leading English articles ("the", "a", "an") should be ignored when
    ///     sorting albums, artists, and songs alphabetically.
    /// </summary>
    Task<bool> GetIgnoreLeadingArticlesOnSortEnabledAsync();

    Task SetIgnoreLeadingArticlesOnSortEnabledAsync(bool isEnabled);

    event Action<bool>? IgnoreLeadingArticlesOnSortEnabledChanged;

    Task ResetToDefaultsAsync();


    /// <summary>
    ///     Gets the configured service providers for a specific category.
    /// </summary>
    /// <param name="category">The service category to retrieve.</param>
    /// <returns>A list of service provider settings, ordered by priority.</returns>
    Task<List<ServiceProviderSetting>> GetServiceProvidersAsync(ServiceCategory category);

    Task SetServiceProvidersAsync(ServiceCategory category, List<ServiceProviderSetting> providers);

    /// <summary>
    ///     Gets enabled service providers for a category, sorted by priority (lowest Order first).
    /// </summary>
    /// <param name="category">The service category to retrieve.</param>
    /// <returns>A list of enabled service providers, sorted by priority.</returns>
    Task<List<ServiceProviderSetting>> GetEnabledServiceProvidersAsync(ServiceCategory category);
}
