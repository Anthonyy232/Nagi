using Microsoft.UI.Xaml;
using Nagi.Models;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Nagi.Services.Abstractions;

/// <summary>
/// Defines a service for managing application-wide settings.
/// </summary>
public interface ISettingsService {
    /// <summary>
    /// Occurs when the player animation setting is changed.
    /// The boolean parameter indicates whether the animation is enabled.
    /// </summary>
    event Action<bool>? PlayerAnimationSettingChanged;

    /// <summary>
    /// Occurs when the "Hide to Tray" setting is changed.
    /// The boolean parameter indicates whether hiding to tray is enabled.
    /// </summary>
    event Action<bool>? HideToTraySettingChanged;

    /// <summary>
    /// Occurs when the "Show Cover Art in Tray Flyout" setting is changed.
    /// The boolean parameter indicates whether the cover art is visible.
    /// </summary>
    event Action<bool>? ShowCoverArtInTrayFlyoutSettingChanged;

    /// <summary>
    /// Occurs when the navigation view item settings have changed.
    /// </summary>
    event Action? NavigationSettingsChanged;

    /// <summary>
    /// Gets the initial volume level for the media player.
    /// </summary>
    /// <returns>A volume level between 0.0 and 1.0.</returns>
    Task<double> GetInitialVolumeAsync();

    /// <summary>
    /// Saves the current volume level.
    /// </summary>
    /// <param name="volume">The volume level to save, clamped between 0.0 and 1.0.</param>
    Task SaveVolumeAsync(double volume);

    /// <summary>
    /// Gets the initial mute state for the media player.
    /// </summary>
    /// <returns>True if the player should be muted; otherwise, false.</returns>
    Task<bool> GetInitialMuteStateAsync();

    /// <summary>
    /// Saves the current mute state.
    /// </summary>
    /// <param name="isMuted">The mute state to save.</param>
    Task SaveMuteStateAsync(bool isMuted);

    /// <summary>
    /// Gets the initial shuffle state for music playback.
    /// </summary>
    /// <returns>True if shuffle mode is enabled; otherwise, false.</returns>
    Task<bool> GetInitialShuffleStateAsync();

    /// <summary>
    /// Saves the current shuffle state.
    /// </summary>
    /// <param name="isEnabled">The shuffle state to save.</param>
    Task SaveShuffleStateAsync(bool isEnabled);

    /// <summary>
    /// Gets the initial repeat mode for music playback.
    /// </summary>
    /// <returns>The saved <see cref="RepeatMode" />.</returns>
    Task<RepeatMode> GetInitialRepeatModeAsync();

    /// <summary>
    /// Saves the current repeat mode.
    /// </summary>
    /// <param name="mode">The repeat mode to save.</param>
    Task SaveRepeatModeAsync(RepeatMode mode);

    /// <summary>
    /// Gets the current application theme (Light, Dark, or Default).
    /// </summary>
    /// <returns>The saved <see cref="ElementTheme" />.</returns>
    Task<ElementTheme> GetThemeAsync();

    /// <summary>
    /// Sets the application theme.
    /// </summary>
    /// <param name="theme">The theme to apply and save.</param>
    Task SetThemeAsync(ElementTheme theme);

    /// <summary>
    /// Gets whether dynamic theming (based on album art) is enabled.
    /// </summary>
    /// <returns>True if dynamic theming is enabled; otherwise, false.</returns>
    Task<bool> GetDynamicThemingAsync();

    /// <summary>
    /// Sets the dynamic theming preference.
    /// </summary>
    /// <param name="isEnabled">The dynamic theming preference to save.</param>
    Task SetDynamicThemingAsync(bool isEnabled);

    /// <summary>
    /// Gets whether player bar animations are enabled.
    /// </summary>
    /// <returns>True if player animations are enabled; otherwise, false.</returns>
    Task<bool> GetPlayerAnimationEnabledAsync();

    /// <summary>
    /// Sets the player bar animation preference.
    /// </summary>
    /// <param name="isEnabled">The player animation preference to save.</param>
    Task SetPlayerAnimationEnabledAsync(bool isEnabled);

    /// <summary>
    /// Gets whether the application should restore playback state on launch.
    /// </summary>
    /// <returns>True if restoring playback state is enabled; otherwise, false.</returns>
    Task<bool> GetRestorePlaybackStateEnabledAsync();

    /// <summary>
    /// Sets the restore playback state preference.
    /// </summary>
    /// <param name="isEnabled">The restore playback state preference to save.</param>
    Task SetRestorePlaybackStateEnabledAsync(bool isEnabled);

    /// <summary>
    /// Gets whether the application should launch automatically on system startup.
    /// </summary>
    /// <returns>True if auto-launch is enabled; otherwise, false.</returns>
    Task<bool> GetAutoLaunchEnabledAsync();

    /// <summary>
    /// Sets the auto-launch preference.
    /// </summary>
    /// <param name="isEnabled">The auto-launch preference to save.</param>
    Task SetAutoLaunchEnabledAsync(bool isEnabled);

    /// <summary>
    /// Gets whether the application should start minimized.
    /// </summary>
    /// <returns>True if start minimized is enabled; otherwise, false.</returns>
    Task<bool> GetStartMinimizedEnabledAsync();

    /// <summary>
    /// Sets the start minimized preference.
    /// </summary>
    /// <param name="isEnabled">The start minimized preference to save.</param>
    Task SetStartMinimizedEnabledAsync(bool isEnabled);

    /// <summary>
    /// Gets whether the application should hide to the system tray when closed.
    /// </summary>
    /// <returns>True if hide to tray is enabled; otherwise, false.</returns>
    Task<bool> GetHideToTrayEnabledAsync();

    /// <summary>
    /// Sets the hide to tray preference.
    /// </summary>
    /// <param name="isEnabled">The hide to tray preference to save.</param>
    Task SetHideToTrayEnabledAsync(bool isEnabled);

    /// <summary>
    /// Gets whether cover art should be shown in the tray flyout.
    /// </summary>
    /// <returns>True if showing cover art is enabled; otherwise, false.</returns>
    Task<bool> GetShowCoverArtInTrayFlyoutAsync();

    /// <summary>
    /// Sets the preference for showing cover art in the tray flyout.
    /// </summary>
    /// <param name="isEnabled">The preference to save.</param>
    Task SetShowCoverArtInTrayFlyoutAsync(bool isEnabled);

    /// <summary>
    /// Gets whether the application should fetch additional metadata from online services.
    /// </summary>
    /// <returns>True if fetching online metadata is enabled; otherwise, false.</returns>
    Task<bool> GetFetchOnlineMetadataEnabledAsync();

    /// <summary>
    /// Sets the preference for fetching additional metadata from online services.
    /// </summary>
    /// <param name="isEnabled">The preference to save.</param>
    Task SetFetchOnlineMetadataEnabledAsync(bool isEnabled);


    /// <summary>
    /// Gets the ordered and enabled/disabled list of navigation items.
    /// </summary>
    /// <returns>A list of <see cref="NavigationItemSetting" />.</returns>
    Task<List<NavigationItemSetting>> GetNavigationItemsAsync();

    /// <summary>
    /// Saves the ordered and enabled/disabled list of navigation items.
    /// </summary>
    /// <param name="items">The list of <see cref="NavigationItemSetting" /> to save.</param>
    Task SetNavigationItemsAsync(List<NavigationItemSetting> items);

    /// <summary>
    /// Resets all settings to their default values.
    /// </summary>
    Task ResetToDefaultsAsync();

    /// <summary>
    /// Saves the current playback state, including the queue and current track.
    /// </summary>
    /// <param name="state">The playback state to save. If null, the saved state is cleared.</param>
    Task SavePlaybackStateAsync(PlaybackState? state);

    /// <summary>
    /// Retrieves the last saved playback state.
    /// </summary>
    /// <returns>The saved <see cref="PlaybackState" />, or null if none exists.</returns>
    Task<PlaybackState?> GetPlaybackStateAsync();

    /// <summary>
    /// Explicitly clears any saved playback state.
    /// </summary>
    Task ClearPlaybackStateAsync();
}