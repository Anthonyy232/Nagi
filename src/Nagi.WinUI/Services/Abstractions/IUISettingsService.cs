using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Nagi.Core.Services.Abstractions;
using Nagi.WinUI.Models;
using Nagi.WinUI.Navigation;

namespace Nagi.WinUI.Services.Abstractions;

/// <summary>
///     Defines a service for managing application-wide settings, including UI-specific ones.
///     This interface extends the core <see cref="ISettingsService" />.
/// </summary>
public interface IUISettingsService : ISettingsService
{
    event Action<bool>? PlayerAnimationSettingChanged;

    event Action<bool>? HideToTraySettingChanged;

    event Action<bool>? MinimizeToMiniPlayerSettingChanged;

    event Action<bool>? ShowCoverArtInTrayFlyoutSettingChanged;

    event Action? NavigationSettingsChanged;

    event Action? PlayerButtonSettingsChanged;

    event Action<QueueDisplaySettings>? QueueDisplaySettingsChanged;

    event Action<bool>? TransparencyEffectsSettingChanged;

    event Action<BackdropMaterial>? BackdropMaterialChanged;

    event Action? PlayerDesignSettingsChanged;

    event Action<int>? SongsPerPageChanged;

    /// <summary>
    ///     Gets whether system-wide transparency effects are currently enabled.
    ///     This is a live value from the OS, not a stored setting.
    /// </summary>
    /// <returns>True if transparency effects are enabled; otherwise, false.</returns>
    bool IsTransparencyEffectsEnabled();

    Task<ElementTheme> GetThemeAsync();

    Task SetThemeAsync(ElementTheme theme);

    Task<BackdropMaterial> GetBackdropMaterialAsync();

    Task SetBackdropMaterialAsync(BackdropMaterial material);

    Task<PlayerBackgroundMaterial> GetPlayerBackgroundMaterialAsync();

    Task SetPlayerBackgroundMaterialAsync(PlayerBackgroundMaterial material);

    /// <summary>
    ///     Gets the currently configured player tint intensity (0.0 to 1.0).
    /// </summary>
    /// <returns>A value between 0.0 (neutral) and 1.0 (vibrant).</returns>
    Task<double> GetPlayerTintIntensityAsync();

    /// <summary>
    ///     Saves the player tint intensity.
    /// </summary>
    /// <param name="intensity">A value between 0.0 and 1.0.</param>
    Task SetPlayerTintIntensityAsync(double intensity);

    Task<bool> GetDynamicThemingAsync();

    Task SetDynamicThemingAsync(bool isEnabled);

    Task<bool> GetPlayerAnimationEnabledAsync();

    Task SetPlayerAnimationEnabledAsync(bool isEnabled);

    Task<bool> GetAutoLaunchEnabledAsync();

    Task SetAutoLaunchEnabledAsync(bool isEnabled);

    Task<bool> GetStartMinimizedEnabledAsync();

    Task SetStartMinimizedEnabledAsync(bool isEnabled);

    Task<bool> GetHideToTrayEnabledAsync();

    Task SetHideToTrayEnabledAsync(bool isEnabled);

    Task<bool> GetMinimizeToMiniPlayerEnabledAsync();

    Task SetMinimizeToMiniPlayerEnabledAsync(bool isEnabled);

    Task<bool> GetShowCoverArtInTrayFlyoutAsync();

    Task SetShowCoverArtInTrayFlyoutAsync(bool isEnabled);

    /// <summary>
    ///     Gets the ordered and enabled/disabled list of navigation items.
    /// </summary>
    /// <returns>A list of <see cref="NavigationItemSetting" />.</returns>
    Task<List<NavigationItemSetting>> GetNavigationItemsAsync();

    /// <summary>
    ///     Saves the ordered and enabled/disabled list of navigation items.
    /// </summary>
    /// <param name="items">The list of <see cref="NavigationItemSetting" /> to save.</param>
    Task SetNavigationItemsAsync(List<NavigationItemSetting> items);

    /// <summary>
    ///     Gets the ordered and enabled/disabled list of player control buttons.
    /// </summary>
    /// <returns>A list of <see cref="PlayerButtonSetting" />.</returns>
    Task<List<PlayerButtonSetting>> GetPlayerButtonSettingsAsync();

    /// <summary>
    ///     Saves the ordered and enabled/disabled list of player control buttons.
    /// </summary>
    /// <param name="settings">The list of <see cref="PlayerButtonSetting" /> to save.</param>
    Task SetPlayerButtonSettingsAsync(List<PlayerButtonSetting> settings);

    Task<QueueDisplaySettings> GetQueueDisplaySettingsAsync();
    Task SetQueueDisplaySettingsAsync(QueueDisplaySettings settings);

    List<PlayerButtonSetting> GetDefaultPlayerButtonSettings();

    Task<bool> GetRememberWindowSizeEnabledAsync();

    Task SetRememberWindowSizeEnabledAsync(bool isEnabled);

    /// <summary>
    ///     Gets the last saved main window size.
    /// </summary>
    /// <returns>A tuple of (width, height), or null if no size has been saved.</returns>
    Task<(int Width, int Height)?> GetLastWindowSizeAsync();

    /// <summary>
    ///     Saves the main window size.
    /// </summary>
    /// <param name="width">The window width in pixels.</param>
    /// <param name="height">The window height in pixels.</param>
    Task SetLastWindowSizeAsync(int width, int height);

    Task<bool> GetRememberWindowPositionEnabledAsync();

    Task SetRememberWindowPositionEnabledAsync(bool isEnabled);

    /// <summary>
    ///     Gets the last saved main window position.
    /// </summary>
    /// <returns>A tuple of (x, y), or null if no position has been saved.</returns>
    Task<(int X, int Y)?> GetLastWindowPositionAsync();

    /// <summary>
    ///     Saves the main window position.
    /// </summary>
    /// <param name="x">The window X position in pixels.</param>
    /// <param name="y">The window Y position in pixels.</param>
    Task SetLastWindowPositionAsync(int x, int y);

    Task<bool> GetRememberPaneStateEnabledAsync();

    Task SetRememberPaneStateEnabledAsync(bool isEnabled);

    /// <summary>
    ///     Gets the last saved navigation pane open/closed state.
    /// </summary>
    /// <returns>True if the pane was open, false if closed, or null if no state has been saved.</returns>
    Task<bool?> GetLastPaneOpenAsync();

    Task SetLastPaneOpenAsync(bool isOpen);

    /// <summary>
    ///     Gets the current application language override.
    /// </summary>
    /// <returns>The BCP-47 language tag, or empty string for "Auto".</returns>
    Task<string> GetLanguageAsync();

    /// <summary>
    ///     Sets the application language override.
    /// </summary>
    /// <param name="languageCode">The BCP-47 language tag, or empty string for "Auto".</param>
    Task SetLanguageAsync(string languageCode);


    /// <summary>
    ///     Gets the user-defined accent color, if any.
    /// </summary>
    /// <returns>The saved <see cref="Windows.UI.Color" />, or null if none has been saved.</returns>
    Task<Windows.UI.Color?> GetAccentColorAsync();

    /// <summary>
    ///     Saves the user-defined accent color.
    /// </summary>
    /// <param name="color">The color to save, or null to clear the setting.</param>
    Task SetAccentColorAsync(Windows.UI.Color? color);

    Task<TEnum> GetSortOrderAsync<TEnum>(string pageKey) where TEnum : struct, Enum;
    Task SetSortOrderAsync<TEnum>(string pageKey, TEnum sortOrder) where TEnum : struct, Enum;

    Task<int> GetSongsPerPageAsync();

    Task SetSongsPerPageAsync(int songsPerPage);
}
