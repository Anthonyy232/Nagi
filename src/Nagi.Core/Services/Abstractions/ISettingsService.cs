﻿using Nagi.Core.Models;
using Nagi.Core.Services.Data;

namespace Nagi.Core.Services.Abstractions;

/// <summary>
///     Defines a service for managing application-wide, non-UI settings.
///     This interface is safe to use in the Core project.
/// </summary>
public interface ISettingsService
{
    /// <summary>
    ///     Occurs when Last.fm related settings (scrobbling, now playing) have changed.
    /// </summary>
    event Action? LastFmSettingsChanged;

    /// <summary>
    ///     Occurs when the Discord Rich Presence setting is changed.
    ///     The boolean parameter indicates whether Discord Rich Presence is enabled.
    /// </summary>
    event Action<bool>? DiscordRichPresenceSettingChanged;

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

    /// <summary>
    ///     Gets the initial mute state for the media player.
    /// </summary>
    /// <returns>True if the player should be muted; otherwise, false.</returns>
    Task<bool> GetInitialMuteStateAsync();

    /// <summary>
    ///     Saves the current mute state.
    /// </summary>
    /// <param name="isMuted">The mute state to save.</param>
    Task SaveMuteStateAsync(bool isMuted);

    /// <summary>
    ///     Gets the initial shuffle state for music playback.
    /// </summary>
    /// <returns>True if shuffle mode is enabled; otherwise, false.</returns>
    Task<bool> GetInitialShuffleStateAsync();

    /// <summary>
    ///     Saves the current shuffle state.
    /// </summary>
    /// <param name="isEnabled">The shuffle state to save.</param>
    Task SaveShuffleStateAsync(bool isEnabled);

    /// <summary>
    ///     Gets the initial repeat mode for music playback.
    /// </summary>
    /// <returns>The saved <see cref="RepeatMode" />.</returns>
    Task<RepeatMode> GetInitialRepeatModeAsync();

    /// <summary>
    ///     Saves the current repeat mode.
    /// </summary>
    /// <param name="mode">The repeat mode to save.</param>
    Task SaveRepeatModeAsync(RepeatMode mode);

    /// <summary>
    ///     Gets whether the application should restore playback state on launch.
    /// </summary>
    /// <returns>True if restoring playback state is enabled; otherwise, false.</returns>
    Task<bool> GetRestorePlaybackStateEnabledAsync();

    /// <summary>
    ///     Sets the restore playback state preference.
    /// </summary>
    /// <param name="isEnabled">The restore playback state preference to save.</param>
    Task SetRestorePlaybackStateEnabledAsync(bool isEnabled);

    /// <summary>
    ///     Gets whether the application should fetch additional metadata from online services.
    /// </summary>
    /// <returns>True if fetching online metadata is enabled; otherwise, false.</returns>
    Task<bool> GetFetchOnlineMetadataEnabledAsync();

    /// <summary>
    ///     Sets the preference for fetching additional metadata from online services.
    /// </summary>
    /// <param name="isEnabled">The preference to save.</param>
    Task SetFetchOnlineMetadataEnabledAsync(bool isEnabled);

    /// <summary>
    ///     Gets whether Discord Rich Presence is enabled.
    /// </summary>
    /// <returns>True if Discord Rich Presence is enabled; otherwise, false.</returns>
    Task<bool> GetDiscordRichPresenceEnabledAsync();

    /// <summary>
    ///     Sets the Discord Rich Presence preference.
    /// </summary>
    /// <param name="isEnabled">The Discord Rich Presence preference to save.</param>
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

    /// <summary>
    ///     Explicitly clears any saved playback state.
    /// </summary>
    Task ClearPlaybackStateAsync();

    /// <summary>
    ///     Gets whether scrobbling to Last.fm is enabled.
    /// </summary>
    /// <returns>True if scrobbling is enabled; otherwise, false.</returns>
    Task<bool> GetLastFmScrobblingEnabledAsync();

    /// <summary>
    ///     Sets the preference for scrobbling to Last.fm.
    /// </summary>
    /// <param name="isEnabled">The preference to save.</param>
    Task SetLastFmScrobblingEnabledAsync(bool isEnabled);

    /// <summary>
    ///     Gets whether updating the 'Now Playing' status on Last.fm is enabled.
    /// </summary>
    /// <returns>True if 'Now Playing' updates are enabled; otherwise, false.</returns>
    Task<bool> GetLastFmNowPlayingEnabledAsync();

    /// <summary>
    ///     Sets the preference for updating the 'Now Playing' status on Last.fm.
    /// </summary>
    /// <param name="isEnabled">The preference to save.</param>
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

    /// <summary>
    ///     Removes the saved Last.fm credentials from secure storage.
    /// </summary>
    Task ClearLastFmCredentialsAsync();

    /// <summary>
    ///     Saves the temporary authentication token received from Last.fm.
    /// </summary>
    /// <param name="token">The token to save, or null to clear it.</param>
    Task SaveLastFmAuthTokenAsync(string? token);

    /// <summary>
    ///     Gets the temporary authentication token for Last.fm.
    /// </summary>
    /// <returns>The saved token, or null if not present.</returns>
    Task<string?> GetLastFmAuthTokenAsync();
}