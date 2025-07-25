using Microsoft.UI.Xaml;
using Nagi.Core.Services.Abstractions;
using Nagi.WinUI.Navigation;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Nagi.WinUI.Services.Abstractions;

/// <summary>
/// Defines a service for managing application-wide settings, including UI-specific ones.
/// This interface extends the core ISettingsService.
/// </summary>
public interface IUISettingsService : ISettingsService {
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
    /// Gets whether the application should automatically check for updates on startup.
    /// </summary>
    /// <returns>True if automatic checks are enabled; otherwise, false.</returns>
    Task<bool> GetCheckForUpdatesEnabledAsync();

    /// <summary>
    /// Sets the preference for automatically checking for updates on startup.
    /// </summary>
    /// <param name="isEnabled">The preference to save.</param>
    Task SetCheckForUpdatesEnabledAsync(bool isEnabled);

    /// <summary>
    /// Gets the version string of the last update the user chose to skip.
    /// </summary>
    /// <returns>The version string, or null if no version has been skipped.</returns>
    Task<string?> GetLastSkippedUpdateVersionAsync();

    /// <summary>
    /// Saves the version string of an update the user has chosen to skip.
    /// </summary>
    /// <param name="version">The version string to save, or null to clear the skipped version.</param>
    Task SetLastSkippedUpdateVersionAsync(string? version);

    /// <summary>
    /// Resets all settings to their default values.
    /// </summary>
    Task ResetToDefaultsAsync();
}