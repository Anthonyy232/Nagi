using System.Collections.Generic;
using System.Threading.Tasks;
using Nagi.Core.Services.Abstractions;
using Nagi.Presentation.Models;
using Nagi.Presentation.Navigation;

namespace Nagi.Presentation.Services.Abstractions;

public interface IUISettingsService : ISettingsService
{
    event Action<bool>? PlayerAnimationSettingChanged;

    event Action<bool>? HideToTraySettingChanged;

    event Action<bool>? MinimizeToMiniPlayerSettingChanged;

    event Action<bool>? ShowCoverArtInTrayFlyoutSettingChanged;

    event Action? NavigationSettingsChanged;

    event Action? PlayerButtonSettingsChanged;

    event Action<QueueDisplaySettings>? QueueDisplaySettingsChanged;

    event Action<SongListColumns>? SongListColumnsChanged;

    event Action<bool>? ShowSongListHeaderSettingChanged;

    event Action<BackdropMaterial>? BackdropMaterialChanged;

    event Action? PlayerDesignSettingsChanged;

    event Action<int>? SongsPerPageChanged;

    Task<AppTheme> GetThemeAsync();

    Task SetThemeAsync(AppTheme theme);

    Task<BackdropMaterial> GetBackdropMaterialAsync();

    Task SetBackdropMaterialAsync(BackdropMaterial material);

    Task<PlayerBackgroundMaterial> GetPlayerBackgroundMaterialAsync();

    Task SetPlayerBackgroundMaterialAsync(PlayerBackgroundMaterial material);

    /// <returns>From 0 (neutral) to 1 (vibrant).</returns>
    Task<double> GetPlayerTintIntensityAsync();

    Task SetPlayerTintIntensityAsync(double intensity);

    Task<bool> GetDynamicThemingAsync();

    Task SetDynamicThemingAsync(bool isEnabled);

    Task<bool> GetPlayerAnimationEnabledAsync();

    Task SetPlayerAnimationEnabledAsync(bool isEnabled);

    Task<bool> GetStartMinimizedEnabledAsync();

    Task SetStartMinimizedEnabledAsync(bool isEnabled);

    Task<bool> GetHideToTrayEnabledAsync();

    Task SetHideToTrayEnabledAsync(bool isEnabled);

    Task<bool> GetMinimizeToMiniPlayerEnabledAsync();

    Task SetMinimizeToMiniPlayerEnabledAsync(bool isEnabled);

    Task<bool> GetShowCoverArtInTrayFlyoutAsync();

    Task SetShowCoverArtInTrayFlyoutAsync(bool isEnabled);

    Task<List<NavigationItemSetting>> GetNavigationItemsAsync();

    Task SetNavigationItemsAsync(List<NavigationItemSetting> items);

    Task<List<PlayerButtonSetting>> GetPlayerButtonSettingsAsync();

    Task SetPlayerButtonSettingsAsync(List<PlayerButtonSetting> settings);

    Task<QueueDisplaySettings> GetQueueDisplaySettingsAsync();
    Task SetQueueDisplaySettingsAsync(QueueDisplaySettings settings);

    Task<SongListColumns> GetSongListColumnsAsync();
    Task SetSongListColumnsAsync(SongListColumns columns);

    Task<bool> GetShowSongListHeaderAsync();
    Task SetShowSongListHeaderAsync(bool isEnabled);

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
    /// <returns>The saved color, or null if none has been saved.</returns>
    Task<ArgbColor?> GetAccentColorAsync();

    /// <summary>
    ///     Saves the user-defined accent color.
    /// </summary>
    /// <param name="color">The color to save, or null to clear the setting.</param>
    Task SetAccentColorAsync(ArgbColor? color);

    Task<TEnum> GetSortOrderAsync<TEnum>(string pageKey) where TEnum : struct, Enum;
    Task SetSortOrderAsync<TEnum>(string pageKey, TEnum sortOrder) where TEnum : struct, Enum;

    Task<int> GetSongsPerPageAsync();

    Task SetSongsPerPageAsync(int songsPerPage);
}
