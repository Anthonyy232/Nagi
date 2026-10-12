using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Nagi.Core.Helpers;
using Nagi.WinUI.Helpers;
using Nagi.WinUI.Models;
using Nagi.WinUI.Navigation;

namespace Nagi.WinUI.Services.Implementations;

public partial class SettingsService
{
    public Task<ElementTheme> GetThemeAsync() => Task.FromResult(GetEnumValue(ThemeKey, SettingsDefaults.Theme));

    public Task SetThemeAsync(ElementTheme theme)
    {
        return SetValueAsync(ThemeKey, theme.ToString());
    }

    public Task<BackdropMaterial> GetBackdropMaterialAsync() => Task.FromResult(GetEnumValue(BackdropMaterialKey, SettingsDefaults.DefaultBackdropMaterial));

    public async Task SetBackdropMaterialAsync(BackdropMaterial material)
    {
        await SetValueAsync(BackdropMaterialKey, material.ToString()).ConfigureAwait(false);
        BackdropMaterialChanged?.Invoke(material);
    }

    public Task<bool> GetDynamicThemingAsync() => Task.FromResult(GetValue(DynamicThemingKey, SettingsDefaults.DynamicThemingEnabled));

    public Task SetDynamicThemingAsync(bool isEnabled)
    {
        return SetValueAsync(DynamicThemingKey, isEnabled);
    }

    public Task<bool> GetPlayerAnimationEnabledAsync() => Task.FromResult(GetValue(PlayerAnimationEnabledKey, SettingsDefaults.PlayerAnimationEnabled));

    public Task SetPlayerAnimationEnabledAsync(bool isEnabled)
    {
        return SetValueAndNotifyAsync(PlayerAnimationEnabledKey, isEnabled, SettingsDefaults.PlayerAnimationEnabled, PlayerAnimationSettingChanged);
    }

    public async Task<bool> GetAutoLaunchEnabledAsync()
    {
        try
        {
            var startupTask = await StartupTask.GetAsync(StartupTaskId).AsTask().ConfigureAwait(false);
            return startupTask.State is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;
        }
        catch (Exception ex)
        {
            if (ex.HResult == unchecked((int)0x80073D5B))
            {
                _logger.LogDebug("Auto-launch task check skipped: The package does not have a mutable directory.");
            }
            else
            {
                _logger.LogWarning(ex, "Failed to get startup task state.");
            }
            return false;
        }
    }

    public async Task SetAutoLaunchEnabledAsync(bool isEnabled)
    {
        var startupTask = await StartupTask.GetAsync(StartupTaskId).AsTask().ConfigureAwait(false);
        if (isEnabled)
        {
            var state = await startupTask.RequestEnableAsync().AsTask().ConfigureAwait(false);
            if (state is not StartupTaskState.Enabled and not StartupTaskState.EnabledByPolicy)
                _logger.LogWarning(
                    "StartupTask enable request did not result in 'Enabled' state. Current state: {State}", state);
        }
        else
        {
            startupTask.Disable();
        }
    }

    public Task<bool> GetStartMinimizedEnabledAsync() => Task.FromResult(GetValue(StartMinimizedEnabledKey, SettingsDefaults.StartMinimizedEnabled));

    public Task SetStartMinimizedEnabledAsync(bool isEnabled)
    {
        return SetValueAsync(StartMinimizedEnabledKey, isEnabled);
    }

    public Task<bool> GetHideToTrayEnabledAsync() => Task.FromResult(GetValue(HideToTrayEnabledKey, SettingsDefaults.HideToTrayEnabled));

    public Task SetHideToTrayEnabledAsync(bool isEnabled)
    {
        return SetValueAndNotifyAsync(HideToTrayEnabledKey, isEnabled, SettingsDefaults.HideToTrayEnabled, HideToTraySettingChanged);
    }

    public Task<bool> GetMinimizeToMiniPlayerEnabledAsync() => Task.FromResult(GetValue(MinimizeToMiniPlayerEnabledKey, SettingsDefaults.MinimizeToMiniPlayerEnabled));

    public Task SetMinimizeToMiniPlayerEnabledAsync(bool isEnabled)
    {
        return SetValueAndNotifyAsync(MinimizeToMiniPlayerEnabledKey, isEnabled, SettingsDefaults.MinimizeToMiniPlayerEnabled,
            MinimizeToMiniPlayerSettingChanged);
    }

    public Task<bool> GetShowCoverArtInTrayFlyoutAsync() => Task.FromResult(GetValue(ShowCoverArtInTrayFlyoutKey, SettingsDefaults.ShowCoverArtInTrayFlyoutEnabled));

    public Task SetShowCoverArtInTrayFlyoutAsync(bool isEnabled)
    {
        return SetValueAndNotifyAsync(ShowCoverArtInTrayFlyoutKey, isEnabled, SettingsDefaults.ShowCoverArtInTrayFlyoutEnabled,
            ShowCoverArtInTrayFlyoutSettingChanged);
    }

    public Task<Windows.UI.Color?> GetAccentColorAsync()
    {
        var hex = GetValue<string?>(AccentColorKey, null);
        if (string.IsNullOrEmpty(hex)) return Task.FromResult<Windows.UI.Color?>(null);
        if (HexColorParser.TryParse(hex, out var color))
            return Task.FromResult<Windows.UI.Color?>(color);
        return Task.FromResult<Windows.UI.Color?>(null);
    }

    public Task SetAccentColorAsync(Windows.UI.Color? color)
    {
        if (color == null)
        {
            return SetValueAsync<string?>(AccentColorKey, null);
        }

        var hex = $"#{color.Value.A:X2}{color.Value.R:X2}{color.Value.G:X2}{color.Value.B:X2}";
        return SetValueAsync(AccentColorKey, hex);
    }

    public Task<List<NavigationItemSetting>> GetNavigationItemsAsync()
    {
        var saved = GetComplexValue<List<NavigationItemSetting>>(NavigationItemsKey) ?? [];
        var defaults = GetDefaultNavigationItems();
        var items = saved.DistinctBy(item => item.Tag, StringComparer.OrdinalIgnoreCase)
            .Join(defaults, item => item.Tag, item => item.Tag, (configuration, definition) =>
            {
                definition.IsEnabled = configuration.IsEnabled;
                return definition;
            }, StringComparer.OrdinalIgnoreCase)
            .Concat(defaults)
            .DistinctBy(item => item.Tag, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return Task.FromResult(items);
    }

    public async Task SetNavigationItemsAsync(List<NavigationItemSetting> items)
    {
        await SetValueAsync(NavigationItemsKey, items).ConfigureAwait(false);
        NavigationSettingsChanged?.Invoke();
    }

    public Task<List<PlayerButtonSetting>> GetPlayerButtonSettingsAsync()
    {
        var saved = GetComplexValue<List<PlayerButtonSetting>>(PlayerButtonSettingsKey);
        var defaults = GetDefaultPlayerButtonSettings();
        if (saved is null) return Task.FromResult(defaults);
        var settings = saved.DistinctBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
            .Join(defaults, item => item.Id, item => item.Id, (configuration, definition) =>
            {
                definition.IsEnabled = configuration.IsEnabled;
                return definition;
            }, StringComparer.OrdinalIgnoreCase).ToList();

        // Older saved layouts predate the divider between playback and auxiliary buttons.
        if (!settings.Any(item => item.Id == "Separator"))
            settings.Insert(Math.Min(5, settings.Count), defaults.Single(item => item.Id == "Separator"));
        return Task.FromResult(settings);
    }

    public async Task SetPlayerButtonSettingsAsync(List<PlayerButtonSetting> settings)
    {
        await SetValueAsync(PlayerButtonSettingsKey, settings).ConfigureAwait(false);
        PlayerButtonSettingsChanged?.Invoke();
    }

    public Task<QueueDisplaySettings> GetQueueDisplaySettingsAsync() =>
        Task.FromResult(GetComplexValue<QueueDisplaySettings>(QueueDisplaySettingsKey) ?? new());

    public async Task SetQueueDisplaySettingsAsync(QueueDisplaySettings settings)
    {
        await SetValueAsync(QueueDisplaySettingsKey, settings).ConfigureAwait(false);
        QueueDisplaySettingsChanged?.Invoke(settings);
    }

    public Task<bool> GetRememberWindowSizeEnabledAsync() => Task.FromResult(GetValue(RememberWindowSizeEnabledKey, SettingsDefaults.RememberWindowSizeEnabled));

    public Task SetRememberWindowSizeEnabledAsync(bool isEnabled)
    {
        return SetValueAsync(RememberWindowSizeEnabledKey, isEnabled);
    }

    public Task<(int Width, int Height)?> GetLastWindowSizeAsync()
    {
        var sizeData = GetComplexValue<int[]>(LastWindowSizeKey);
        return Task.FromResult<(int Width, int Height)?>(sizeData is { Length: 2 } ? (sizeData[0], sizeData[1]) : null);
    }

    public Task SetLastWindowSizeAsync(int width, int height)
    {
        return SetValueAsync(LastWindowSizeKey, new[] { width, height });
    }

    public Task<bool> GetRememberWindowPositionEnabledAsync() => Task.FromResult(GetValue(RememberWindowPositionEnabledKey, SettingsDefaults.RememberWindowPositionEnabled));

    public Task SetRememberWindowPositionEnabledAsync(bool isEnabled)
    {
        return SetValueAsync(RememberWindowPositionEnabledKey, isEnabled);
    }

    public Task<(int X, int Y)?> GetLastWindowPositionAsync()
    {
        var positionData = GetComplexValue<int[]>(LastWindowPositionKey);
        return Task.FromResult<(int X, int Y)?>(positionData is { Length: 2 } ? (positionData[0], positionData[1]) : null);
    }

    public Task SetLastWindowPositionAsync(int x, int y)
    {
        return SetValueAsync(LastWindowPositionKey, new[] { x, y });
    }

    public Task<bool> GetRememberPaneStateEnabledAsync() => Task.FromResult(GetValue(RememberPaneStateEnabledKey, SettingsDefaults.RememberPaneStateEnabled));

    public Task SetRememberPaneStateEnabledAsync(bool isEnabled)
    {
        return SetValueAsync(RememberPaneStateEnabledKey, isEnabled);
    }

    public Task<bool?> GetLastPaneOpenAsync()
    {
        var value = GetValue<object?>(LastPaneOpenKey, null);
        return Task.FromResult<bool?>(value switch
        {
            bool isOpen => isOpen,
            string legacy when bool.TryParse(legacy, out var isOpen) => isOpen,
            _ => null
        });
    }

    public Task SetLastPaneOpenAsync(bool isOpen)
    {
        return SetValueAsync(LastPaneOpenKey, isOpen);
    }

    public Task<bool> GetVolumeNormalizationEnabledAsync() => Task.FromResult(GetValue(VolumeNormalizationEnabledKey, SettingsDefaults.VolumeNormalizationEnabled));

    public Task SetVolumeNormalizationEnabledAsync(bool isEnabled)
    {
        return SetValueAndNotifyAsync(VolumeNormalizationEnabledKey, isEnabled, SettingsDefaults.VolumeNormalizationEnabled,
            VolumeNormalizationEnabledChanged);
    }

    public Task<bool> GetWasapiExclusiveModeEnabledAsync() =>
        Task.FromResult(GetValue(WasapiExclusiveModeEnabledKey, SettingsDefaults.WasapiExclusiveModeEnabled));

    public Task SetWasapiExclusiveModeEnabledAsync(bool isEnabled) =>
        SetValueAsync(WasapiExclusiveModeEnabledKey, isEnabled);

    public Task<string> GetAudioOutputDeviceIdAsync() =>
        Task.FromResult(GetValue(AudioOutputDeviceIdKey, string.Empty));

    public Task SetAudioOutputDeviceIdAsync(string deviceId) =>
        SetValueAsync(AudioOutputDeviceIdKey, deviceId);

    public Task<bool> GetFadeOnPlayPauseEnabledAsync() => Task.FromResult(GetValue(FadeOnPlayPauseEnabledKey, SettingsDefaults.FadeOnPlayPauseEnabled));

    public Task SetFadeOnPlayPauseEnabledAsync(bool isEnabled)
    {
        return SetValueAndNotifyAsync(FadeOnPlayPauseEnabledKey, isEnabled, SettingsDefaults.FadeOnPlayPauseEnabled, FadeOnPlayPauseEnabledChanged);
    }

    public Task<int> GetFadeInDurationMsAsync() => Task.FromResult(GetValue("FadeInDurationMs", SettingsDefaults.DefaultFadeInDurationMs));

    public Task SetFadeInDurationMsAsync(int durationMs)
    {
        return SetValueAndNotifyAsync("FadeInDurationMs", durationMs, SettingsDefaults.DefaultFadeInDurationMs, FadeInDurationChanged);
    }

    public Task<int> GetFadeOutDurationMsAsync() => Task.FromResult(GetValue("FadeOutDurationMs", SettingsDefaults.DefaultFadeOutDurationMs));

    public Task SetFadeOutDurationMsAsync(int durationMs)
    {
        return SetValueAndNotifyAsync("FadeOutDurationMs", durationMs, SettingsDefaults.DefaultFadeOutDurationMs, FadeOutDurationChanged);
    }

    public Task<TEnum> GetSortOrderAsync<TEnum>(string pageKey) where TEnum : struct, Enum
    {
        var defaultValue = GetDefaultSortOrder<TEnum>(pageKey);
        return Task.FromResult(GetEnumValue(pageKey, defaultValue));
    }

    private static readonly FrozenDictionary<string, object> _defaultSortOrders = new Dictionary<string, object>()
    {
        { SortOrderHelper.LibrarySortOrderKey, SettingsDefaults.LibrarySortOrder },
        { SortOrderHelper.AlbumsSortOrderKey, SettingsDefaults.AlbumsSortOrder },
        { SortOrderHelper.ArtistsSortOrderKey, SettingsDefaults.ArtistsSortOrder },
        { SortOrderHelper.GenresSortOrderKey, SettingsDefaults.GenresSortOrder },
        { SortOrderHelper.PlaylistsSortOrderKey, SettingsDefaults.PlaylistsSortOrder },
        { SortOrderHelper.FolderViewSortOrderKey, SettingsDefaults.FolderViewSortOrder },
        { SortOrderHelper.AlbumViewSortOrderKey, SettingsDefaults.AlbumViewSortOrder },
        { SortOrderHelper.ArtistViewSortOrderKey, SettingsDefaults.ArtistViewSortOrder },
        { SortOrderHelper.GenreViewSortOrderKey, SettingsDefaults.GenreViewSortOrder }
    }.ToFrozenDictionary();

    private static TEnum GetDefaultSortOrder<TEnum>(string pageKey) where TEnum : struct, Enum
    {
        if (_defaultSortOrders.TryGetValue(pageKey, out var defaultValue) && defaultValue is TEnum enumValue)
            return enumValue;
        return default;
    }

    public Task SetSortOrderAsync<TEnum>(string pageKey, TEnum sortOrder) where TEnum : struct, Enum
    {
        return SetValueAsync(pageKey, sortOrder.ToString());
    }
}
