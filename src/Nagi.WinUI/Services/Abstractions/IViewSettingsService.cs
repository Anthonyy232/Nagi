using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Nagi.WinUI.Navigation;

namespace Nagi.WinUI.Services.Abstractions;

/// <summary>
///     Defines a service for managing view-specific settings related to the application's
///     appearance and behavior within the Windows UI shell.
/// </summary>
public interface IViewSettingsService
{
    event Action<bool>? PlayerAnimationSettingChanged;
    event Action<bool>? HideToTraySettingChanged;
    event Action<bool>? ShowCoverArtInTrayFlyoutSettingChanged;
    event Action? NavigationSettingsChanged;

    Task<ElementTheme> GetThemeAsync();
    Task SetThemeAsync(ElementTheme theme);
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
    Task<bool> GetShowCoverArtInTrayFlyoutAsync();
    Task SetShowCoverArtInTrayFlyoutAsync(bool isEnabled);
    Task<List<NavigationItemSetting>> GetNavigationItemsAsync();
    Task SetNavigationItemsAsync(List<NavigationItemSetting> items);

    /// <summary>
    ///     Resets all application settings (both core and view) to their default values.
    /// </summary>
    Task ResetAllSettingsAsync();
}