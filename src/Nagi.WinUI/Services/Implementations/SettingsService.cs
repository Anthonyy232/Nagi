using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.Storage;
using Windows.UI.ViewManagement;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Nagi.Core.Helpers;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;
using Nagi.WinUI.Models;
using Nagi.WinUI.Navigation;
using Nagi.WinUI.Services.Abstractions;

namespace Nagi.WinUI.Services.Implementations;

/// <summary>
///     Manages application settings by persisting them to local storage using ApplicationData.
/// </summary>
public class SettingsService : IUISettingsService, IDisposable
{
    private const string StartupTaskId = "NagiAutolaunchStartup";
    private const string VolumeKey = "AppVolume";
    private const string MuteStateKey = "AppMuteState";
    private const string ShuffleStateKey = "MusicShuffleState";
    private const string RepeatModeKey = "MusicRepeatMode";
    private const string ThemeKey = "AppTheme";
    private const string BackdropMaterialKey = "BackdropMaterial";
    private const string DynamicThemingKey = "DynamicThemingEnabled";
    private const string PlayerAnimationEnabledKey = "PlayerAnimationEnabled";
    private const string RestorePlaybackStateEnabledKey = "RestorePlaybackStateEnabled";
    private const string StartMinimizedEnabledKey = "StartMinimizedEnabled";
    private const string HideToTrayEnabledKey = "HideToTrayEnabled";
    private const string MinimizeToMiniPlayerEnabledKey = "MinimizeToMiniPlayerEnabled";
    private const string ShowQueueButtonEnabledKey = "ShowQueueButtonEnabled";
    private const string ShowCoverArtInTrayFlyoutKey = "ShowCoverArtInTrayFlyout";
    private const string FetchOnlineMetadataKey = "FetchOnlineMetadataEnabled";
    private const string FetchOnlineLyricsEnabledKey = "FetchOnlineLyricsEnabled";
    private const string DiscordRichPresenceEnabledKey = "DiscordRichPresenceEnabled";
    private const string NavigationItemsKey = "NavigationItems";
    private const string PlayerButtonSettingsKey = "PlayerButtonSettings";
    private const string LastFmCredentialResource = "Nagi/LastFm";
    private const string LastFmAuthTokenKey = "LastFmAuthToken";
    private const string LastFmScrobblingEnabledKey = "LastFmScrobblingEnabled";
    private const string LastFmNowPlayingEnabledKey = "LastFmNowPlayingEnabled";
    private const string EqualizerSettingsKey = "EqualizerSettings";
    private const string RememberWindowSizeEnabledKey = "RememberWindowSizeEnabled";
    private const string LastWindowSizeKey = "LastWindowSize";
    private const string RememberWindowPositionEnabledKey = "RememberWindowPositionEnabled";
    private const string LastWindowPositionKey = "LastWindowPosition";
    private const string RememberPaneStateEnabledKey = "RememberPaneStateEnabled";
    private const string LastPaneOpenKey = "LastPaneOpen";
    private const string VolumeNormalizationEnabledKey = "VolumeNormalizationEnabled";
    private const string FadeOnPlayPauseEnabledKey = "FadeOnPlayPauseEnabled";
    private const string AccentColorKey = "AccentColor";
    private const string LyricsServiceProvidersKey = "LyricsServiceProviders";
    private const string MetadataServiceProvidersKey = "MetadataServiceProviders";
    private const string ArtistSplitCharactersKey = "ArtistSplitCharacters";
    private const string LanguageKey = "AppLanguage";
    private const string PlayerBackgroundMaterialKey = "PlayerBackgroundMaterial";
    private const string PlayerTintIntensityKey = "PlayerTintIntensity";
    private const string SongsPerPageKey = "SongsPerPage";
    private const string GenreSplitCharactersKey = "GenreSplitCharacters";

    private static readonly JsonSerializerOptions _serializerOptions = new() { WriteIndented = true };
    private readonly ICredentialLockerService _credentialLockerService;
    private readonly ApplicationDataContainer? _localSettings;
    private readonly ILogger<SettingsService> _logger;
    private readonly IPathConfiguration _pathConfig;
    private readonly UISettings _uiSettings = new();
    private readonly IDispatcherService _dispatcherService;
    private bool _disposed;

    public SettingsService(IPathConfiguration pathConfig, ICredentialLockerService credentialLockerService,
        ILogger<SettingsService> logger, IDispatcherService dispatcherService)
    {
        _pathConfig = pathConfig ?? throw new ArgumentNullException(nameof(pathConfig));
        _credentialLockerService =
            credentialLockerService ?? throw new ArgumentNullException(nameof(credentialLockerService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _dispatcherService = dispatcherService ?? throw new ArgumentNullException(nameof(dispatcherService));

        _localSettings = ApplicationData.Current.LocalSettings;

        _uiSettings.AdvancedEffectsEnabledChanged += OnAdvancedEffectsEnabledChanged;
    }


    public event Action<bool>? PlayerAnimationSettingChanged;
    public event Action<bool>? HideToTraySettingChanged;
    public event Action<bool>? MinimizeToMiniPlayerSettingChanged;
    public event Action<bool>? ShowQueueButtonSettingChanged;
    public event Action<bool>? ShowCoverArtInTrayFlyoutSettingChanged;
    public event Action? NavigationSettingsChanged;
    public event Action? PlayerButtonSettingsChanged;
    public event Action? LastFmSettingsChanged;
    public event Action<bool>? DiscordRichPresenceSettingChanged;
    public event Action<bool>? VolumeNormalizationEnabledChanged;
    public event Action<bool>? FadeOnPlayPauseEnabledChanged;
    public event Action<int>? FadeInDurationChanged;
    public event Action<int>? FadeOutDurationChanged;
    public event Action<bool>? TransparencyEffectsSettingChanged;
    public event Action<BackdropMaterial>? BackdropMaterialChanged;
    public event Action<bool>? FetchOnlineMetadataEnabledChanged;
    public event Action<bool>? FetchOnlineLyricsEnabledChanged;
    public event Action<ServiceCategory>? ServiceProvidersChanged;
    public event Action? ArtistSplitCharactersChanged;
    public event Action<string>? LanguageChanged;
    public event Action? PlayerDesignSettingsChanged;
    public event Action<int>? SongsPerPageChanged;
    public event Action? GenreSplitCharactersChanged;

    public bool IsTransparencyEffectsEnabled()
    {
        return _uiSettings.AdvancedEffectsEnabled;
    }

    public async Task ResetToDefaultsAsync()
    {
        _localSettings!.Values.Clear();


        var tasks = new List<Task>
        {
            ClearPlaybackStateAsync(),
            SetAutoLaunchEnabledAsync(SettingsDefaults.AutoLaunchEnabled),
            SetPlayerAnimationEnabledAsync(SettingsDefaults.PlayerAnimationEnabled),
            SetShowQueueButtonEnabledAsync(SettingsDefaults.ShowQueueButtonEnabled),
            SetHideToTrayEnabledAsync(SettingsDefaults.HideToTrayEnabled),
            SetMinimizeToMiniPlayerEnabledAsync(SettingsDefaults.MinimizeToMiniPlayerEnabled),
            SetShowCoverArtInTrayFlyoutAsync(SettingsDefaults.ShowCoverArtInTrayFlyoutEnabled),
            SetFetchOnlineMetadataEnabledAsync(SettingsDefaults.FetchOnlineMetadataEnabled),
            SetFetchOnlineLyricsEnabledAsync(SettingsDefaults.FetchOnlineLyricsEnabled),
            SetDiscordRichPresenceEnabledAsync(SettingsDefaults.DiscordRichPresenceEnabled),
            SetThemeAsync(SettingsDefaults.Theme),
            SetBackdropMaterialAsync(SettingsDefaults.DefaultBackdropMaterial),
            SetDynamicThemingAsync(SettingsDefaults.DynamicThemingEnabled),
            SetRestorePlaybackStateEnabledAsync(SettingsDefaults.RestorePlaybackStateEnabled),
            SetStartMinimizedEnabledAsync(SettingsDefaults.StartMinimizedEnabled),
            SetNavigationItemsAsync(GetDefaultNavigationItems()),
            SetPlayerButtonSettingsAsync(GetDefaultPlayerButtonSettings()),
            SaveVolumeAsync(SettingsDefaults.Volume),
            SaveMuteStateAsync(SettingsDefaults.MuteState),
            SaveShuffleStateAsync(SettingsDefaults.ShuffleState),
            SaveRepeatModeAsync(SettingsDefaults.DefaultRepeatMode),
            SetLastFmScrobblingEnabledAsync(SettingsDefaults.LastFmScrobblingEnabled),
            SetLastFmNowPlayingEnabledAsync(SettingsDefaults.LastFmNowPlayingEnabled),
            ClearLastFmCredentialsAsync(),
            SetEqualizerSettingsAsync(new EqualizerSettings()),
            SetRememberWindowSizeEnabledAsync(SettingsDefaults.RememberWindowSizeEnabled),
            SetRememberWindowPositionEnabledAsync(SettingsDefaults.RememberWindowPositionEnabled),
            SetRememberPaneStateEnabledAsync(SettingsDefaults.RememberPaneStateEnabled),
            SetVolumeNormalizationEnabledAsync(SettingsDefaults.VolumeNormalizationEnabled),
            SetFadeOnPlayPauseEnabledAsync(SettingsDefaults.FadeOnPlayPauseEnabled),
            SetFadeInDurationMsAsync(SettingsDefaults.DefaultFadeInDurationMs),
            SetFadeOutDurationMsAsync(SettingsDefaults.DefaultFadeOutDurationMs),
            SetAccentColorAsync(SettingsDefaults.AccentColor),
            SetArtistSplitCharactersAsync(SettingsDefaults.DefaultArtistSplitCharacters),
            SetLanguageAsync(string.Empty),
            SetPlayerBackgroundMaterialAsync(SettingsDefaults.DefaultPlayerBackgroundMaterial),
            SetPlayerTintIntensityAsync(SettingsDefaults.DefaultPlayerTintIntensity),
            SetSongsPerPageAsync(SettingsDefaults.DefaultSongsPerPage),
            SetGenreSplitCharactersAsync(SettingsDefaults.DefaultGenreSplitCharacters)
        };

        await Task.WhenAll(tasks).ConfigureAwait(false);

        _logger.LogInformation("All application settings have been reset to their default values.");
    }


    private void OnAdvancedEffectsEnabledChanged(UISettings sender, object args)
    {
        _dispatcherService.TryEnqueue(() => TransparencyEffectsSettingChanged?.Invoke(_uiSettings.AdvancedEffectsEnabled));
    }



    private T GetValue<T>(string key, T defaultValue)
    {
        return _localSettings!.Values.TryGetValue(key, out var value) && value is T v ? v : defaultValue;
    }

    private async Task<T?> GetComplexValueAsync<T>(string key) where T : class
    {
        string? json = null;

        if (_localSettings!.Values.TryGetValue(key, out var value) && value is string jsonString) json = jsonString;

        if (json != null)
            try
            {
                return JsonSerializer.Deserialize<T>(json);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Failed to deserialize complex value for key '{Key}'.", key);
                return null;
            }

        return null;
    }

    private TEnum GetEnumValue<TEnum>(string key, TEnum defaultValue) where TEnum : struct, Enum
    {
        string? name = null;

        if (_localSettings!.Values.TryGetValue(key, out var value) && value is string stringValue)
            name = stringValue;

        if (name != null && Enum.TryParse(name, out TEnum result)) return result;

        return defaultValue;
    }

    private Task SetValueAsync<T>(string key, T value)
    {
        if (typeof(T).IsClass && typeof(T) != typeof(string))
            _localSettings!.Values[key] = JsonSerializer.Serialize(value, _serializerOptions);
        else
            _localSettings!.Values[key] = value!;

        return Task.CompletedTask;
    }

    private async Task SetValueAndNotifyAsync<T>(string key, T newValue, T defaultValue, Action<T>? notifier)
    {
        var currentValue = GetValue(key, defaultValue);

        await SetValueAsync(key, newValue).ConfigureAwait(false);
        if (!EqualityComparer<T>.Default.Equals(currentValue, newValue)) notifier?.Invoke(newValue);
    }

    private List<NavigationItemSetting> GetDefaultNavigationItems()
    {
        return new List<NavigationItemSetting>
        {
            new() { DisplayName = Resources.Strings.Settings_Nav_Library, Tag = "Library", IconGlyph = "\uE1D3", IsEnabled = true },
            new() { DisplayName = Resources.Strings.Settings_Nav_Folders, Tag = "Folders", IconGlyph = "\uE8B7", IsEnabled = true },
            new() { DisplayName = Resources.Strings.Settings_Nav_Playlists, Tag = "Playlists", IconGlyph = "\uE90B", IsEnabled = true },
            new() { DisplayName = Resources.Strings.Settings_Nav_Artists, Tag = "Artists", IconGlyph = "\uE77B", IsEnabled = true },
            new() { DisplayName = Resources.Strings.Settings_Nav_Albums, Tag = "Albums", IconGlyph = "\uE93C", IsEnabled = true },
            new() { DisplayName = Resources.Strings.Settings_Nav_Genres, Tag = "Genres", IconGlyph = "\uE8EC", IsEnabled = true },
            new() { DisplayName = Resources.Strings.Settings_Nav_Insights, Tag = "Insights", IconGlyph = "\uE9D9", IsEnabled = false }
        };
    }

    public List<PlayerButtonSetting> GetDefaultPlayerButtonSettings()
    {
        return new List<PlayerButtonSetting>
        {
            new() { Id = "Shuffle", DisplayName = Resources.Strings.Settings_Button_Shuffle, IconGlyph = "\uE8B1", IsEnabled = true },
            new() { Id = "Previous", DisplayName = Resources.Strings.Settings_Button_Previous, IconGlyph = "\uE892", IsEnabled = true },
            new() { Id = "PlayPause", DisplayName = Resources.Strings.Settings_Button_PlayPause, IconGlyph = "\uE768", IsEnabled = true },
            new() { Id = "Next", DisplayName = Resources.Strings.Settings_Button_Next, IconGlyph = "\uE893", IsEnabled = true },
            new() { Id = "Repeat", DisplayName = Resources.Strings.Settings_Button_Repeat, IconGlyph = "\uE8EE", IsEnabled = true },
            new() { Id = "Separator", DisplayName = Resources.Strings.Settings_Button_Divider, IconGlyph = "\uE7A3", IsEnabled = true },
            new() { Id = "Lyrics", DisplayName = Resources.Strings.Settings_Button_Lyrics, IconGlyph = "\uE8D2", IsEnabled = true },
            new() { Id = "Queue", DisplayName = Resources.Strings.Settings_Button_Queue, IconGlyph = "\uE90B", IsEnabled = true },
            new() { Id = "Volume", DisplayName = Resources.Strings.Settings_Button_Volume, IconGlyph = "\uE767", IsEnabled = true }
        };
    }

    #region Core Settings (ISettingsService)

    public Task<double> GetInitialVolumeAsync()
    {
        return Task.FromResult(Math.Clamp(GetValue(VolumeKey, SettingsDefaults.Volume), 0.0, 1.0));
    }

    public Task SaveVolumeAsync(double volume)
    {
        return SetValueAsync(VolumeKey, Math.Clamp(volume, 0.0, 1.0));
    }

    public Task<bool> GetInitialMuteStateAsync()
    {
        return Task.FromResult(GetValue(MuteStateKey, SettingsDefaults.MuteState));
    }

    public Task SaveMuteStateAsync(bool isMuted)
    {
        return SetValueAsync(MuteStateKey, isMuted);
    }

    public Task<bool> GetInitialShuffleStateAsync()
    {
        return Task.FromResult(GetValue(ShuffleStateKey, SettingsDefaults.ShuffleState));
    }

    public Task SaveShuffleStateAsync(bool isEnabled)
    {
        return SetValueAsync(ShuffleStateKey, isEnabled);
    }

    public Task<RepeatMode> GetInitialRepeatModeAsync()
    {
        return Task.FromResult(GetEnumValue(RepeatModeKey, SettingsDefaults.DefaultRepeatMode));
    }

    public Task SaveRepeatModeAsync(RepeatMode mode)
    {
        return SetValueAsync(RepeatModeKey, mode.ToString());
    }

    public Task<bool> GetRestorePlaybackStateEnabledAsync()
    {
        return Task.FromResult(GetValue(RestorePlaybackStateEnabledKey, SettingsDefaults.RestorePlaybackStateEnabled));
    }

    public Task SetRestorePlaybackStateEnabledAsync(bool isEnabled)
    {
        return SetValueAsync(RestorePlaybackStateEnabledKey, isEnabled);
    }

    public Task<bool> GetFetchOnlineMetadataEnabledAsync()
    {
        return Task.FromResult(GetValue(FetchOnlineMetadataKey, SettingsDefaults.FetchOnlineMetadataEnabled));
    }

    public Task SetFetchOnlineMetadataEnabledAsync(bool isEnabled)
    {
        return SetValueAndNotifyAsync(FetchOnlineMetadataKey, isEnabled, SettingsDefaults.FetchOnlineMetadataEnabled, FetchOnlineMetadataEnabledChanged);
    }

    public Task<bool> GetFetchOnlineLyricsEnabledAsync()
    {
        return Task.FromResult(GetValue(FetchOnlineLyricsEnabledKey, SettingsDefaults.FetchOnlineLyricsEnabled));
    }

    public Task SetFetchOnlineLyricsEnabledAsync(bool isEnabled)
    {
        return SetValueAndNotifyAsync(FetchOnlineLyricsEnabledKey, isEnabled, SettingsDefaults.FetchOnlineLyricsEnabled, FetchOnlineLyricsEnabledChanged);
    }

    public Task<bool> GetDiscordRichPresenceEnabledAsync()
    {
        return Task.FromResult(GetValue(DiscordRichPresenceEnabledKey, SettingsDefaults.DiscordRichPresenceEnabled));
    }

    public Task SetDiscordRichPresenceEnabledAsync(bool isEnabled)
    {
        return SetValueAndNotifyAsync(DiscordRichPresenceEnabledKey, isEnabled, SettingsDefaults.DiscordRichPresenceEnabled,
            DiscordRichPresenceSettingChanged);
    }

    public async Task SavePlaybackStateAsync(PlaybackState? state)
    {
        if (state == null)
        {
            await ClearPlaybackStateAsync().ConfigureAwait(false);
            return;
        }

        var jsonState = JsonSerializer.Serialize(state, _serializerOptions);

        var stateFile = await ApplicationData.Current.LocalFolder.CreateFileAsync(
            Path.GetFileName(_pathConfig.PlaybackStateFilePath), CreationCollisionOption.ReplaceExisting).AsTask().ConfigureAwait(false);
        await FileIO.WriteTextAsync(stateFile, jsonState).AsTask().ConfigureAwait(false);
    }

    public async Task<PlaybackState?> GetPlaybackStateAsync()
    {
        try
        {
            string? jsonState = null;
            var item = await ApplicationData.Current.LocalFolder.TryGetItemAsync(
                Path.GetFileName(_pathConfig.PlaybackStateFilePath)).AsTask().ConfigureAwait(false);
            if (item is IStorageFile stateFile) jsonState = await FileIO.ReadTextAsync(stateFile).AsTask().ConfigureAwait(false);

            if (string.IsNullOrEmpty(jsonState)) return null;
            return JsonSerializer.Deserialize<PlaybackState>(jsonState);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error reading PlaybackState.");
            await ClearPlaybackStateAsync().ConfigureAwait(false);
            return null;
        }
    }

    public async Task ClearPlaybackStateAsync()
    {
        try
        {
            var item = await ApplicationData.Current.LocalFolder.TryGetItemAsync(
                Path.GetFileName(_pathConfig.PlaybackStateFilePath)).AsTask().ConfigureAwait(false);
            if (item != null) await item.DeleteAsync().AsTask().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error clearing PlaybackState file.");
        }
    }

    public Task<bool> GetLastFmScrobblingEnabledAsync()
    {
        return Task.FromResult(GetValue(LastFmScrobblingEnabledKey, SettingsDefaults.LastFmScrobblingEnabled));
    }

    public async Task SetLastFmScrobblingEnabledAsync(bool isEnabled)
    {
        await SetValueAsync(LastFmScrobblingEnabledKey, isEnabled).ConfigureAwait(false);
        LastFmSettingsChanged?.Invoke();
    }

    public Task<bool> GetLastFmNowPlayingEnabledAsync()
    {
        return Task.FromResult(GetValue(LastFmNowPlayingEnabledKey, SettingsDefaults.LastFmNowPlayingEnabled));
    }

    public async Task SetLastFmNowPlayingEnabledAsync(bool isEnabled)
    {
        await SetValueAsync(LastFmNowPlayingEnabledKey, isEnabled).ConfigureAwait(false);
        LastFmSettingsChanged?.Invoke();
    }

    public Task<(string? Username, string? SessionKey)?> GetLastFmCredentialsAsync()
    {
        return Task.FromResult(_credentialLockerService.RetrieveCredential(LastFmCredentialResource));
    }

    public Task SaveLastFmCredentialsAsync(string username, string sessionKey)
    {
        _credentialLockerService.SaveCredential(LastFmCredentialResource, username, sessionKey);
        return Task.CompletedTask;
    }

    public async Task ClearLastFmCredentialsAsync()
    {
        _credentialLockerService.RemoveCredential(LastFmCredentialResource);
        await SetLastFmScrobblingEnabledAsync(false).ConfigureAwait(false);
        await SetLastFmNowPlayingEnabledAsync(false).ConfigureAwait(false);
        await SaveLastFmAuthTokenAsync(null).ConfigureAwait(false);
    }

    public Task SaveLastFmAuthTokenAsync(string? token)
    {
        return SetValueAsync(LastFmAuthTokenKey, token);
    }

    public Task<string?> GetLastFmAuthTokenAsync()
    {
        return Task.FromResult(GetValue<string?>(LastFmAuthTokenKey, null));
    }

    public Task<EqualizerSettings?> GetEqualizerSettingsAsync()
    {
        return GetComplexValueAsync<EqualizerSettings>(EqualizerSettingsKey);
    }

    public Task SetEqualizerSettingsAsync(EqualizerSettings settings)
    {
        return SetValueAsync(EqualizerSettingsKey, settings);
    }

    public async Task<List<ServiceProviderSetting>> GetServiceProvidersAsync(ServiceCategory category)
    {
        var key = category == ServiceCategory.Lyrics ? LyricsServiceProvidersKey : MetadataServiceProvidersKey;
        var items = await GetComplexValueAsync<List<ServiceProviderSetting>>(key).ConfigureAwait(false);


        if (items is { Count: > 0 })
        {
            // Deduplicate items to fix users with duplicate items saved in config file
            var distinctItems = new List<ServiceProviderSetting>();
            var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            
            foreach (var item in items)
            {
                if (seenIds.Add(item.Id))
                {
                    distinctItems.Add(item);
                }
            }

            if (distinctItems.Count != items.Count)
            {
                _ = SetValueAsync(key, distinctItems);
            }

            items = distinctItems;

            // Merge with defaults to handle new services added in updates
            var defaults = GetDefaultServiceProviders(category);
            var knownIds = defaults.Select(d => d.Id).ToHashSet();
            
            // Filter out unknown providers (handles removal of providers in future versions)
            items = items.Where(i => knownIds.Contains(i.Id)).ToList();
            
            var existingIds = items.Select(i => i.Id).ToHashSet();
            var newProviders = defaults.Where(d => !existingIds.Contains(d.Id)).ToList();

            if (newProviders.Count > 0)
            {
                // Append new providers at the end with lowest priority
                var maxOrder = items.Count > 0 ? items.Max(i => i.Order) : -1;
                foreach (var provider in newProviders)
                {
                    provider.Order = ++maxOrder;
                    items.Add(provider);
                }
            }

            // Migration: Update NetEase display name if it's the old default
            foreach (var item in items.Where(i => i.Id == ServiceProviderIds.NetEase && i.DisplayName == "NetEase Music 163"))
            {
                item.DisplayName = "NetEase";
            }

            return items.OrderBy(i => i.Order).ToList();
        }

        return GetDefaultServiceProviders(category);
    }

    public async Task SetServiceProvidersAsync(ServiceCategory category, List<ServiceProviderSetting> providers)
    {
        // Normalize order values based on list position
        for (var i = 0; i < providers.Count; i++)
            providers[i].Order = i;

        var key = category == ServiceCategory.Lyrics ? LyricsServiceProvidersKey : MetadataServiceProvidersKey;
        await SetValueAsync(key, providers).ConfigureAwait(false);
        ServiceProvidersChanged?.Invoke(category);
    }

    public async Task<List<ServiceProviderSetting>> GetEnabledServiceProvidersAsync(ServiceCategory category)
    {
        var providers = await GetServiceProvidersAsync(category).ConfigureAwait(false);
        return providers.Where(p => p.IsEnabled).OrderBy(p => p.Order).ToList();
    }

    public Task<string> GetArtistSplitCharactersAsync()
    {
        return Task.FromResult(GetValue(ArtistSplitCharactersKey, SettingsDefaults.DefaultArtistSplitCharacters));
    }

    public async Task SetArtistSplitCharactersAsync(string characters)
    {
        var currentValue = GetValue(ArtistSplitCharactersKey, SettingsDefaults.DefaultArtistSplitCharacters);

        await SetValueAsync(ArtistSplitCharactersKey, characters).ConfigureAwait(false);
        if (currentValue != characters) ArtistSplitCharactersChanged?.Invoke();
    }

    public Task<string> GetGenreSplitCharactersAsync() => Task.FromResult(GetValue(GenreSplitCharactersKey, SettingsDefaults.DefaultGenreSplitCharacters));

    public async Task SetGenreSplitCharactersAsync(string characters)
    {
        var currentValue = GetValue(GenreSplitCharactersKey, SettingsDefaults.DefaultGenreSplitCharacters);

        await SetValueAsync(GenreSplitCharactersKey, characters).ConfigureAwait(false);
        if (currentValue != characters) GenreSplitCharactersChanged?.Invoke();
    }

    public Task<string> GetLanguageAsync() => Task.FromResult(GetValue(LanguageKey, string.Empty));

    public async Task SetLanguageAsync(string languageCode)
    {
        await SetValueAndNotifyAsync(LanguageKey, languageCode, string.Empty, LanguageChanged).ConfigureAwait(false);
    }

    public Task<PlayerBackgroundMaterial> GetPlayerBackgroundMaterialAsync() => Task.FromResult(GetEnumValue(PlayerBackgroundMaterialKey, PlayerBackgroundMaterial.Acrylic));

    public Task SetPlayerBackgroundMaterialAsync(PlayerBackgroundMaterial material)
    {
        return SetValueAndNotifyAsync(PlayerBackgroundMaterialKey, material.ToString(), PlayerBackgroundMaterial.Acrylic.ToString(), _ => PlayerDesignSettingsChanged?.Invoke());
    }

    public Task<double> GetPlayerTintIntensityAsync() => Task.FromResult(GetValue(PlayerTintIntensityKey, 1.0));

    public Task SetPlayerTintIntensityAsync(double intensity)
    {
        return SetValueAndNotifyAsync(PlayerTintIntensityKey, Math.Clamp(intensity, 0.0, 1.0), 1.0, _ => PlayerDesignSettingsChanged?.Invoke());
    }

    public Task<int> GetSongsPerPageAsync() => Task.FromResult(GetValue(SongsPerPageKey, SettingsDefaults.DefaultSongsPerPage));

    public Task SetSongsPerPageAsync(int songsPerPage)
    {
        return SetValueAndNotifyAsync(SongsPerPageKey, songsPerPage, SettingsDefaults.DefaultSongsPerPage, SongsPerPageChanged);
    }

    private static List<ServiceProviderSetting> GetDefaultServiceProviders(ServiceCategory category)
    {
        return category switch
        {
            ServiceCategory.Lyrics => new List<ServiceProviderSetting>
            {
                new()
                {
                    Id = ServiceProviderIds.LrcLib,
                    DisplayName = "LRCLIB",
                    Category = ServiceCategory.Lyrics,
                    IsEnabled = true,
                    Order = 0,
                    Description = Resources.Strings.Settings_Provider_LRCLIB_Desc
                },
                new()
                {
                    Id = ServiceProviderIds.NetEase,
                    DisplayName = "NetEase",
                    Category = ServiceCategory.Lyrics,
                    IsEnabled = true,
                    Order = 1,
                    Description = Resources.Strings.Settings_Provider_NetEase_Desc
                }
            },
            ServiceCategory.Metadata => new List<ServiceProviderSetting>
            {
                new()
                {
                    Id = ServiceProviderIds.MusicBrainz,
                    DisplayName = "MusicBrainz",
                    Category = ServiceCategory.Metadata,
                    IsEnabled = true,
                    Order = 0,
                    Description = Resources.Strings.Settings_Provider_MusicBrainz_Desc
                },
                new()
                {
                    Id = ServiceProviderIds.TheAudioDb,
                    DisplayName = "TheAudioDB",
                    Category = ServiceCategory.Metadata,
                    IsEnabled = true,
                    Order = 1,
                    Description = Resources.Strings.Settings_Provider_TheAudioDB_Desc
                },
                new()
                {
                    Id = ServiceProviderIds.FanartTv,
                    DisplayName = "Fanart.tv",
                    Category = ServiceCategory.Metadata,
                    IsEnabled = true,
                    Order = 2,
                    Description = Resources.Strings.Settings_Provider_FanartTv_Desc
                },
                new()
                {
                    Id = ServiceProviderIds.Spotify,
                    DisplayName = "Spotify",
                    Category = ServiceCategory.Metadata,
                    IsEnabled = true,
                    Order = 3,
                    Description = Resources.Strings.Settings_Provider_Spotify_Desc
                },
                new()
                {
                    Id = ServiceProviderIds.LastFm,
                    DisplayName = "Last.fm",
                    Category = ServiceCategory.Metadata,
                    IsEnabled = true,
                    Order = 4,
                    Description = Resources.Strings.Settings_Provider_LastFm_Desc
                }
            },
            _ => new List<ServiceProviderSetting>()
        };
    }

    #endregion

    #region UI Settings (IUISettingsService)

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
            return startupTask.State is StartupTaskState.Enabled;
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

    public Task<bool> GetShowQueueButtonEnabledAsync() => Task.FromResult(GetValue(ShowQueueButtonEnabledKey, SettingsDefaults.ShowQueueButtonEnabled));

    public Task SetShowQueueButtonEnabledAsync(bool isEnabled)
    {
        return SetValueAndNotifyAsync(ShowQueueButtonEnabledKey, isEnabled, SettingsDefaults.ShowQueueButtonEnabled, ShowQueueButtonSettingChanged);
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
        if (App.CurrentApp!.TryParseHexColor(hex, out var color))
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

    public async Task<List<NavigationItemSetting>> GetNavigationItemsAsync()
    {
        var items = await GetComplexValueAsync<List<NavigationItemSetting>>(NavigationItemsKey).ConfigureAwait(false);

        if (items != null)
        {
            // Deduplicate items to fix users with duplicate items saved in config file
            var distinctItems = new List<NavigationItemSetting>();
            var seenTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            
            foreach (var item in items)
            {
                if (seenTags.Add(item.Tag))
                {
                    distinctItems.Add(item);
                }
            }

            if (distinctItems.Count != items.Count)
            {
                _ = SetValueAsync(NavigationItemsKey, distinctItems);
            }

            items = distinctItems;

            var defaultItems = GetDefaultNavigationItems();
            var loadedTags = new HashSet<string>(items.Select(i => i.Tag));
            var missingItems = defaultItems.Where(d => !loadedTags.Contains(d.Tag));
            items.AddRange(missingItems);

            // Refresh localization for all items
            foreach (var item in items) RefreshNavigationItemLocalization(item);

            return items;
        }

        return GetDefaultNavigationItems();
    }

    public async Task SetNavigationItemsAsync(List<NavigationItemSetting> items)
    {
        await SetValueAsync(NavigationItemsKey, items).ConfigureAwait(false);
        NavigationSettingsChanged?.Invoke();
    }

    public async Task<List<PlayerButtonSetting>> GetPlayerButtonSettingsAsync()
    {
        var settings = await GetComplexValueAsync<List<PlayerButtonSetting>>(PlayerButtonSettingsKey).ConfigureAwait(false);

        if (settings != null)
        {
            // Deduplicate items to fix users with duplicate items saved in config file
            var distinctSettings = new List<PlayerButtonSetting>();
            var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            
            foreach (var setting in settings)
            {
                if (seenIds.Add(setting.Id))
                {
                    distinctSettings.Add(setting);
                }
            }

            if (distinctSettings.Count != settings.Count)
            {
                _ = SetValueAsync(PlayerButtonSettingsKey, distinctSettings);
            }

            settings = distinctSettings;

            // For backward compatibility, ensure the separator exists for users with older settings.
            if (!settings.Any(b => b.Id == "Separator"))
                settings.Insert(5,
                    new PlayerButtonSetting
                        { Id = "Separator", DisplayName = Resources.Strings.Settings_Button_Divider, IconGlyph = "\uE7A3", IsEnabled = true });

            // Refresh localization for all buttons
            foreach (var setting in settings) RefreshPlayerButtonLocalization(setting);

            return settings;
        }

        return GetDefaultPlayerButtonSettings();
    }

    public async Task SetPlayerButtonSettingsAsync(List<PlayerButtonSetting> settings)
    {
        await SetValueAsync(PlayerButtonSettingsKey, settings).ConfigureAwait(false);
        PlayerButtonSettingsChanged?.Invoke();
    }

    private void RefreshNavigationItemLocalization(NavigationItemSetting item)
    {
        var name = item.Tag switch
        {
            "Library" => Resources.Strings.Settings_Nav_Library,
            "Folders" => Resources.Strings.Settings_Nav_Folders,
            "Playlists" => Resources.Strings.Settings_Nav_Playlists,
            "Artists" => Resources.Strings.Settings_Nav_Artists,
            "Albums" => Resources.Strings.Settings_Nav_Albums,
            "Genres" => Resources.Strings.Settings_Nav_Genres,
            "Insights" => Resources.Strings.Settings_Nav_Insights,
            _ => null
        };

        if (!string.IsNullOrEmpty(name)) item.DisplayName = name;
    }

    private void RefreshPlayerButtonLocalization(PlayerButtonSetting item)
    {
        var name = item.Id switch
        {
            "Shuffle" => Resources.Strings.Settings_Button_Shuffle,
            "Previous" => Resources.Strings.Settings_Button_Previous,
            "PlayPause" => Resources.Strings.Settings_Button_PlayPause,
            "Next" => Resources.Strings.Settings_Button_Next,
            "Repeat" => Resources.Strings.Settings_Button_Repeat,
            "Separator" => Resources.Strings.Settings_Button_Divider,
            "Lyrics" => Resources.Strings.Settings_Button_Lyrics,
            "Queue" => Resources.Strings.Settings_Button_Queue,
            "Volume" => Resources.Strings.Settings_Button_Volume,
            _ => null
        };

        if (!string.IsNullOrEmpty(name)) item.DisplayName = name;
    }

    public Task<bool> GetRememberWindowSizeEnabledAsync() => Task.FromResult(GetValue(RememberWindowSizeEnabledKey, SettingsDefaults.RememberWindowSizeEnabled));

    public Task SetRememberWindowSizeEnabledAsync(bool isEnabled)
    {
        return SetValueAsync(RememberWindowSizeEnabledKey, isEnabled);
    }

    public async Task<(int Width, int Height)?> GetLastWindowSizeAsync()
    {
        var sizeData = await GetComplexValueAsync<int[]>(LastWindowSizeKey).ConfigureAwait(false);
        if (sizeData is { Length: 2 })
            return (sizeData[0], sizeData[1]);
        return null;
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

    public async Task<(int X, int Y)?> GetLastWindowPositionAsync()
    {
        var positionData = await GetComplexValueAsync<int[]>(LastWindowPositionKey).ConfigureAwait(false);
        if (positionData is { Length: 2 })
            return (positionData[0], positionData[1]);
        return null;
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
        // Use a string to differentiate between 'never set' and 'set to false'
        var value = GetValue<string?>(LastPaneOpenKey, null);
        if (value == null) return Task.FromResult<bool?>(null);
        return Task.FromResult<bool?>(value == "true");
    }

    public Task SetLastPaneOpenAsync(bool isOpen)
    {
        return SetValueAsync(LastPaneOpenKey, isOpen ? "true" : "false");
    }

    public Task<bool> GetVolumeNormalizationEnabledAsync() => Task.FromResult(GetValue(VolumeNormalizationEnabledKey, SettingsDefaults.VolumeNormalizationEnabled));

    public Task SetVolumeNormalizationEnabledAsync(bool isEnabled)
    {
        return SetValueAndNotifyAsync(VolumeNormalizationEnabledKey, isEnabled, SettingsDefaults.VolumeNormalizationEnabled,
            VolumeNormalizationEnabledChanged);
    }

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

    public Task FlushAsync()
    {
        _logger.LogDebug("FlushAsync: ApplicationData handles persistence automatically.");
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _uiSettings.AdvancedEffectsEnabledChanged -= OnAdvancedEffectsEnabledChanged;
        GC.SuppressFinalize(this);
    }

    #endregion
}
