using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Nagi.Models;

namespace Nagi.Services;

/// <summary>
///     Defines a service for managing application-wide settings.
/// </summary>
public interface ISettingsService
{
    /// <summary>
    ///     Occurs when the player animation setting is changed.
    ///     The boolean parameter indicates whether the animation is enabled.
    /// </summary>
    event Action<bool>? PlayerAnimationSettingChanged;

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
    /// <returns>True if the player should be muted, false otherwise.</returns>
    Task<bool> GetInitialMuteStateAsync();

    /// <summary>
    ///     Saves the current mute state.
    /// </summary>
    /// <param name="isMuted">The mute state to save.</param>
    Task SaveMuteStateAsync(bool isMuted);

    /// <summary>
    ///     Gets the initial shuffle state for music playback.
    /// </summary>
    /// <returns>True if shuffle mode is enabled, false otherwise.</returns>
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
    ///     Gets the current application theme (Light, Dark, or Default).
    /// </summary>
    /// <returns>The saved <see cref="ElementTheme" />.</returns>
    Task<ElementTheme> GetThemeAsync();

    /// <summary>
    ///     Sets the application theme.
    /// </summary>
    /// <param name="theme">The theme to apply and save.</param>
    Task SetThemeAsync(ElementTheme theme);

    /// <summary>
    ///     Gets whether dynamic theming (based on album art) is enabled.
    /// </summary>
    /// <returns>True if dynamic theming is enabled, false otherwise.</returns>
    Task<bool> GetDynamicThemingAsync();

    /// <summary>
    ///     Sets the dynamic theming preference.
    /// </summary>
    /// <param name="isEnabled">The dynamic theming preference to save.</param>
    Task SetDynamicThemingAsync(bool isEnabled);

    /// <summary>
    ///     Gets whether player bar animations are enabled.
    /// </summary>
    /// <returns>True if player animations are enabled, false otherwise.</returns>
    Task<bool> GetPlayerAnimationEnabledAsync();

    /// <summary>
    ///     Sets the player bar animation preference.
    /// </summary>
    /// <param name="isEnabled">The player animation preference to save.</param>
    Task SetPlayerAnimationEnabledAsync(bool isEnabled);

    /// <summary>
    ///     Resets all settings to their default values.
    /// </summary>
    Task ResetToDefaultsAsync();

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
}