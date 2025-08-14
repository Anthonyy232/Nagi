using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.Storage;
using Windows.UI.ViewManagement;
using Microsoft.UI.Xaml;
using Microsoft.Win32;
using Nagi.Core.Helpers;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;
using Nagi.WinUI.Models;
using Nagi.WinUI.Navigation;
using Nagi.WinUI.Services.Abstractions;

namespace Nagi.WinUI.Services.Implementations;

/// <summary>
/// Manages application settings by persisting them to local storage. This implementation supports
/// both packaged (MSIX) and unpackaged deployments, storing settings in the appropriate location.
/// For unpackaged deployments, file writes are debounced to improve performance.
/// </summary>
public class SettingsService : IUISettingsService {
    private const string AppName = "Nagi";
    private const string AutoLaunchRegistryValueName = AppName;
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
    private const string DiscordRichPresenceEnabledKey = "DiscordRichPresenceEnabled";
    private const string NavigationItemsKey = "NavigationItems";
    private const string PlayerButtonSettingsKey = "PlayerButtonSettings";
    private const string CheckForUpdatesEnabledKey = "CheckForUpdatesEnabled";
    private const string LastSkippedUpdateVersionKey = "LastSkippedUpdateVersion";
    private const string LastFmCredentialResource = "Nagi/LastFm";
    private const string LastFmAuthTokenKey = "LastFmAuthToken";
    private const string LastFmScrobblingEnabledKey = "LastFmScrobblingEnabled";
    private const string LastFmNowPlayingEnabledKey = "LastFmNowPlayingEnabled";
    private const string EqualizerSettingsKey = "EqualizerSettings";

    private static readonly JsonSerializerOptions _serializerOptions = new() { WriteIndented = true };
    private readonly ICredentialLockerService _credentialLockerService;
    private readonly bool _isPackaged;
    private readonly ApplicationDataContainer? _localSettings;
    private readonly IPathConfiguration _pathConfig;
    private readonly TimeSpan _saveDebounceDelay = TimeSpan.FromMilliseconds(500);
    private readonly SemaphoreSlim _settingsFileLock = new(1, 1);
    private readonly UISettings _uiSettings = new();

    private Dictionary<string, object?> _settings;
    private volatile bool _isInitialized;
    private int _isSaveQueued;

    public SettingsService(IPathConfiguration pathConfig, ICredentialLockerService credentialLockerService) {
        _pathConfig = pathConfig ?? throw new ArgumentNullException(nameof(pathConfig));
        _credentialLockerService = credentialLockerService ?? throw new ArgumentNullException(nameof(credentialLockerService));
        _isPackaged = pathConfig.IsPackaged;
        _settings = new Dictionary<string, object?>();

        if (_isPackaged) {
            _localSettings = ApplicationData.Current.LocalSettings;
        }

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
    public event Action<bool>? TransparencyEffectsSettingChanged;
    public event Action<BackdropMaterial>? BackdropMaterialChanged;

    public bool IsTransparencyEffectsEnabled() => _uiSettings.AdvancedEffectsEnabled;

    public async Task ResetToDefaultsAsync() {
        if (_isPackaged) {
            _localSettings!.Values.Clear();
        }
        else {
            await EnsureUnpackagedSettingsLoadedAsync();
            _settings.Clear();
            if (File.Exists(_pathConfig.SettingsFilePath)) {
                File.Delete(_pathConfig.SettingsFilePath);
            }
        }

        await ClearPlaybackStateAsync();
        await SetAutoLaunchEnabledAsync(false);
        await SetPlayerAnimationEnabledAsync(true);
        await SetShowQueueButtonEnabledAsync(true);
        await SetHideToTrayEnabledAsync(true);
        await SetMinimizeToMiniPlayerEnabledAsync(false);
        await SetShowCoverArtInTrayFlyoutAsync(true);
        await SetFetchOnlineMetadataEnabledAsync(false);
        await SetDiscordRichPresenceEnabledAsync(false);
        await SetThemeAsync(ElementTheme.Default);
        await SetBackdropMaterialAsync(BackdropMaterial.Mica);
        await SetDynamicThemingAsync(true);
        await SetRestorePlaybackStateEnabledAsync(true);
        await SetStartMinimizedEnabledAsync(false);
        await SetNavigationItemsAsync(GetDefaultNavigationItems());
        await SetPlayerButtonSettingsAsync(GetDefaultPlayerButtonSettings());
        await SaveVolumeAsync(0.5);
        await SaveMuteStateAsync(false);
        await SaveShuffleStateAsync(false);
        await SaveRepeatModeAsync(RepeatMode.Off);
        await SetCheckForUpdatesEnabledAsync(true);
        await SetLastSkippedUpdateVersionAsync(null);
        await ClearLastFmCredentialsAsync();
        await SetEqualizerSettingsAsync(new EqualizerSettings());

        Debug.WriteLine("[SettingsService] All application settings have been reset to their default values.");
    }

    private void OnAdvancedEffectsEnabledChanged(UISettings sender, object args) {
        TransparencyEffectsSettingChanged?.Invoke(_uiSettings.AdvancedEffectsEnabled);
    }

    /// <summary>
    /// For unpackaged deployments, ensures the settings dictionary is loaded from the JSON file on disk.
    /// This operation is thread-safe and will only execute once.
    /// </summary>
    private async Task EnsureUnpackagedSettingsLoadedAsync() {
        if (_isPackaged || _isInitialized) return;

        await _settingsFileLock.WaitAsync();
        try {
            if (_isInitialized) return;

            if (File.Exists(_pathConfig.SettingsFilePath)) {
                var json = await File.ReadAllTextAsync(_pathConfig.SettingsFilePath);
                _settings = JsonSerializer.Deserialize<Dictionary<string, object?>>(json) ?? new Dictionary<string, object?>();
            }

            _isInitialized = true;
        }
        catch (Exception ex) {
            Debug.WriteLine($"[SettingsService] Failed to read settings file. A new one will be created. Error: {ex.Message}");
            _settings = new Dictionary<string, object?>();
            _isInitialized = true;
        }
        finally {
            _settingsFileLock.Release();
        }
    }

    /// <summary>
    /// Queues a request to save the settings to a file for unpackaged deployments.
    /// This operation is debounced to prevent excessive I/O during rapid changes.
    /// </summary>
    private async Task QueueSaveAsync() {
        if (Interlocked.CompareExchange(ref _isSaveQueued, 1, 0) == 0) {
            await Task.Delay(_saveDebounceDelay);

            await _settingsFileLock.WaitAsync();
            try {
                var json = JsonSerializer.Serialize(_settings, _serializerOptions);
                await File.WriteAllTextAsync(_pathConfig.SettingsFilePath, json);
            }
            catch (Exception ex) {
                Debug.WriteLine($"[SettingsService] CRITICAL: Failed to save settings to file. Error: {ex.Message}");
            }
            finally {
                _settingsFileLock.Release();
                Interlocked.Exchange(ref _isSaveQueued, 0);
            }
        }
    }

    private T GetValue<T>(string key, T defaultValue) {
        if (_isPackaged) {
            return _localSettings!.Values.TryGetValue(key, out var value) && value is T v ? v : defaultValue;
        }
        else {
            if (_settings.TryGetValue(key, out var value) && value != null)
                try {
                    if (value is JsonElement element) return element.Deserialize<T>() ?? defaultValue;
                }
                catch (JsonException) {
                    return defaultValue;
                }

            return defaultValue;
        }
    }

    private async Task<T?> GetComplexValueAsync<T>(string key) where T : class {
        string? json = null;

        if (_isPackaged) {
            if (_localSettings!.Values.TryGetValue(key, out var value) && value is string jsonString) {
                json = jsonString;
            }
        }
        else {
            await EnsureUnpackagedSettingsLoadedAsync();
            if (_settings.TryGetValue(key, out var value) && value != null) {
                json = (value as JsonElement?)?.GetRawText() ?? value as string;
            }
        }

        if (json != null) {
            try {
                return JsonSerializer.Deserialize<T>(json);
            }
            catch (JsonException ex) {
                Debug.WriteLine($"[SettingsService] Failed to deserialize complex value for key '{key}'. Error: {ex.Message}");
                return null;
            }
        }

        return null;
    }

    private TEnum GetEnumValue<TEnum>(string key, TEnum defaultValue) where TEnum : struct, Enum {
        string? name = null;

        if (_isPackaged) {
            if (_localSettings!.Values.TryGetValue(key, out var value) && value is string stringValue) {
                name = stringValue;
            }
        }
        else {
            if (_settings.TryGetValue(key, out var value) && value is JsonElement element) {
                name = element.GetString();
            }
        }

        if (name != null && Enum.TryParse(name, out TEnum result)) {
            return result;
        }

        return defaultValue;
    }

    private async Task SetValueAsync<T>(string key, T value) {
        if (_isPackaged) {
            if (typeof(T).IsClass && typeof(T) != typeof(string)) {
                _localSettings!.Values[key] = JsonSerializer.Serialize(value, _serializerOptions);
            }
            else {
                _localSettings!.Values[key] = value;
            }
        }
        else {
            await EnsureUnpackagedSettingsLoadedAsync();

            if (value is null) {
                _settings[key] = null;
            }
            else {
                var serializedValue = JsonSerializer.Serialize(value);
                _settings[key] = JsonDocument.Parse(serializedValue).RootElement.Clone();
            }

            _ = QueueSaveAsync();
        }
    }

    private async Task SetValueAndNotifyAsync<T>(string key, T newValue, T defaultValue, Action<T>? notifier) {
        await EnsureUnpackagedSettingsLoadedAsync();
        var currentValue = GetValue(key, defaultValue);

        if (!EqualityComparer<T>.Default.Equals(currentValue, newValue)) {
            notifier?.Invoke(newValue);
        }
        await SetValueAsync(key, newValue);
    }

    private List<NavigationItemSetting> GetDefaultNavigationItems() {
        return new List<NavigationItemSetting>
        {
            new() { DisplayName = "Library", Tag = "library", IconGlyph = "\uE1D3", IsEnabled = true },
            new() { DisplayName = "Folders", Tag = "folders", IconGlyph = "\uE8B7", IsEnabled = true },
            new() { DisplayName = "Playlists", Tag = "playlists", IconGlyph = "\uE90B", IsEnabled = true },
            new() { DisplayName = "Artists", Tag = "artists", IconGlyph = "\uE77B", IsEnabled = true },
            new() { DisplayName = "Albums", Tag = "albums", IconGlyph = "\uE93C", IsEnabled = true },
            new() { DisplayName = "Genres", Tag = "genres", IconGlyph = "\uE8EC", IsEnabled = true }
        };
    }

    private List<PlayerButtonSetting> GetDefaultPlayerButtonSettings() {
        return new List<PlayerButtonSetting>
        {
            new() { Id = "Shuffle", DisplayName = "Shuffle", IconGlyph = "\uE8B1", IsEnabled = true },
            new() { Id = "Previous", DisplayName = "Previous", IconGlyph = "\uE892", IsEnabled = true },
            new() { Id = "PlayPause", DisplayName = "Play/Pause", IconGlyph = "\uE768", IsEnabled = true },
            new() { Id = "Next", DisplayName = "Next", IconGlyph = "\uE893", IsEnabled = true },
            new() { Id = "Repeat", DisplayName = "Repeat", IconGlyph = "\uE8EE", IsEnabled = true },
            new() { Id = "Separator", DisplayName = "Layout Divider", IconGlyph = "\uE7A3", IsEnabled = true },
            new() { Id = "Lyrics", DisplayName = "Lyrics", IconGlyph = "\uE8D2", IsEnabled = true },
            new() { Id = "Queue", DisplayName = "Queue", IconGlyph = "\uE90B", IsEnabled = true },
            new() { Id = "Volume", DisplayName = "Volume", IconGlyph = "\uE767", IsEnabled = true },
        };
    }

    #region Core Settings (ISettingsService)

    public async Task<double> GetInitialVolumeAsync() {
        await EnsureUnpackagedSettingsLoadedAsync();
        return Math.Clamp(GetValue(VolumeKey, 0.5), 0.0, 1.0);
    }

    public Task SaveVolumeAsync(double volume) => SetValueAsync(VolumeKey, Math.Clamp(volume, 0.0, 1.0));

    public async Task<bool> GetInitialMuteStateAsync() {
        await EnsureUnpackagedSettingsLoadedAsync();
        return GetValue(MuteStateKey, false);
    }

    public Task SaveMuteStateAsync(bool isMuted) => SetValueAsync(MuteStateKey, isMuted);

    public async Task<bool> GetInitialShuffleStateAsync() {
        await EnsureUnpackagedSettingsLoadedAsync();
        return GetValue(ShuffleStateKey, false);
    }

    public Task SaveShuffleStateAsync(bool isEnabled) => SetValueAsync(ShuffleStateKey, isEnabled);

    public async Task<RepeatMode> GetInitialRepeatModeAsync() {
        await EnsureUnpackagedSettingsLoadedAsync();
        return GetEnumValue(RepeatModeKey, RepeatMode.Off);
    }

    public Task SaveRepeatModeAsync(RepeatMode mode) => SetValueAsync(RepeatModeKey, mode.ToString());

    public async Task<bool> GetRestorePlaybackStateEnabledAsync() {
        await EnsureUnpackagedSettingsLoadedAsync();
        return GetValue(RestorePlaybackStateEnabledKey, true);
    }

    public Task SetRestorePlaybackStateEnabledAsync(bool isEnabled) => SetValueAsync(RestorePlaybackStateEnabledKey, isEnabled);

    public async Task<bool> GetFetchOnlineMetadataEnabledAsync() {
        await EnsureUnpackagedSettingsLoadedAsync();
        return GetValue(FetchOnlineMetadataKey, false);
    }

    public Task SetFetchOnlineMetadataEnabledAsync(bool isEnabled) => SetValueAsync(FetchOnlineMetadataKey, isEnabled);

    public async Task<bool> GetDiscordRichPresenceEnabledAsync() {
        await EnsureUnpackagedSettingsLoadedAsync();
        return GetValue(DiscordRichPresenceEnabledKey, false);
    }

    public Task SetDiscordRichPresenceEnabledAsync(bool isEnabled) => SetValueAndNotifyAsync(DiscordRichPresenceEnabledKey, isEnabled, false, DiscordRichPresenceSettingChanged);

    public async Task SavePlaybackStateAsync(PlaybackState? state) {
        if (state == null) {
            await ClearPlaybackStateAsync();
            return;
        }

        var jsonState = JsonSerializer.Serialize(state, _serializerOptions);

        if (_isPackaged) {
            var stateFile = await ApplicationData.Current.LocalFolder.CreateFileAsync(
                Path.GetFileName(_pathConfig.PlaybackStateFilePath), CreationCollisionOption.ReplaceExisting);
            await FileIO.WriteTextAsync(stateFile, jsonState);
        }
        else {
            await File.WriteAllTextAsync(_pathConfig.PlaybackStateFilePath, jsonState);
        }
    }

    public async Task<PlaybackState?> GetPlaybackStateAsync() {
        try {
            string? jsonState = null;
            if (_isPackaged) {
                var item = await ApplicationData.Current.LocalFolder.TryGetItemAsync(Path.GetFileName(_pathConfig.PlaybackStateFilePath));
                if (item is IStorageFile stateFile) {
                    jsonState = await FileIO.ReadTextAsync(stateFile);
                }
            }
            else {
                if (!File.Exists(_pathConfig.PlaybackStateFilePath)) return null;
                jsonState = await File.ReadAllTextAsync(_pathConfig.PlaybackStateFilePath);
            }

            if (string.IsNullOrEmpty(jsonState)) return null;
            return JsonSerializer.Deserialize<PlaybackState>(jsonState);
        }
        catch (FileNotFoundException) {
            return null;
        }
        catch (Exception ex) {
            Debug.WriteLine($"[SettingsService] Error reading PlaybackState: {ex.Message}");
            await ClearPlaybackStateAsync();
            return null;
        }
    }

    public async Task ClearPlaybackStateAsync() {
        try {
            if (_isPackaged) {
                var item = await ApplicationData.Current.LocalFolder.TryGetItemAsync(Path.GetFileName(_pathConfig.PlaybackStateFilePath));
                if (item != null) {
                    await item.DeleteAsync();
                }
            }
            else {
                if (File.Exists(_pathConfig.PlaybackStateFilePath)) {
                    File.Delete(_pathConfig.PlaybackStateFilePath);
                }
            }
        }
        catch (Exception ex) {
            Debug.WriteLine($"[SettingsService] Error clearing PlaybackState file: {ex.Message}");
        }
    }

    public async Task<bool> GetLastFmScrobblingEnabledAsync() {
        await EnsureUnpackagedSettingsLoadedAsync();
        return GetValue(LastFmScrobblingEnabledKey, false);
    }

    public async Task SetLastFmScrobblingEnabledAsync(bool isEnabled) {
        await SetValueAsync(LastFmScrobblingEnabledKey, isEnabled);
        LastFmSettingsChanged?.Invoke();
    }

    public async Task<bool> GetLastFmNowPlayingEnabledAsync() {
        await EnsureUnpackagedSettingsLoadedAsync();
        return GetValue(LastFmNowPlayingEnabledKey, false);
    }

    public async Task SetLastFmNowPlayingEnabledAsync(bool isEnabled) {
        await SetValueAsync(LastFmNowPlayingEnabledKey, isEnabled);
        LastFmSettingsChanged?.Invoke();
    }

    public Task<(string? Username, string? SessionKey)?> GetLastFmCredentialsAsync() {
        return Task.FromResult(_credentialLockerService.RetrieveCredential(LastFmCredentialResource));
    }

    public Task SaveLastFmCredentialsAsync(string username, string sessionKey) {
        _credentialLockerService.SaveCredential(LastFmCredentialResource, username, sessionKey);
        return Task.CompletedTask;
    }

    public async Task ClearLastFmCredentialsAsync() {
        _credentialLockerService.RemoveCredential(LastFmCredentialResource);
        await SetLastFmScrobblingEnabledAsync(false);
        await SetLastFmNowPlayingEnabledAsync(false);
        await SaveLastFmAuthTokenAsync(null);
    }

    public Task SaveLastFmAuthTokenAsync(string? token) => SetValueAsync(LastFmAuthTokenKey, token);

    public async Task<string?> GetLastFmAuthTokenAsync() {
        await EnsureUnpackagedSettingsLoadedAsync();
        return GetValue<string?>(LastFmAuthTokenKey, null);
    }

    public Task<EqualizerSettings?> GetEqualizerSettingsAsync() => GetComplexValueAsync<EqualizerSettings>(EqualizerSettingsKey);
    public Task SetEqualizerSettingsAsync(EqualizerSettings settings) => SetValueAsync(EqualizerSettingsKey, settings);

    #endregion

    #region UI Settings (IUISettingsService)

    public async Task<ElementTheme> GetThemeAsync() {
        await EnsureUnpackagedSettingsLoadedAsync();
        return GetEnumValue(ThemeKey, ElementTheme.Default);
    }

    public Task SetThemeAsync(ElementTheme theme) => SetValueAsync(ThemeKey, theme.ToString());

    public async Task<BackdropMaterial> GetBackdropMaterialAsync() {
        await EnsureUnpackagedSettingsLoadedAsync();
        return GetEnumValue(BackdropMaterialKey, BackdropMaterial.Mica);
    }

    public async Task SetBackdropMaterialAsync(BackdropMaterial material) {
        await SetValueAsync(BackdropMaterialKey, material.ToString());
        BackdropMaterialChanged?.Invoke(material);
    }

    public async Task<bool> GetDynamicThemingAsync() {
        await EnsureUnpackagedSettingsLoadedAsync();
        return GetValue(DynamicThemingKey, true);
    }

    public Task SetDynamicThemingAsync(bool isEnabled) => SetValueAsync(DynamicThemingKey, isEnabled);

    public async Task<bool> GetPlayerAnimationEnabledAsync() {
        await EnsureUnpackagedSettingsLoadedAsync();
        return GetValue(PlayerAnimationEnabledKey, true);
    }

    public Task SetPlayerAnimationEnabledAsync(bool isEnabled) => SetValueAndNotifyAsync(PlayerAnimationEnabledKey, isEnabled, true, PlayerAnimationSettingChanged);

    public async Task<bool> GetAutoLaunchEnabledAsync() {
        if (_isPackaged) {
            var startupTask = await StartupTask.GetAsync(StartupTaskId);
            return startupTask.State is StartupTaskState.Enabled;
        }

        return await Task.Run(() => {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", false);
            return key?.GetValue(AutoLaunchRegistryValueName) != null;
        });
    }

    public async Task SetAutoLaunchEnabledAsync(bool isEnabled) {
        if (_isPackaged) {
            var startupTask = await StartupTask.GetAsync(StartupTaskId);
            if (isEnabled) {
                var state = await startupTask.RequestEnableAsync();
                if (state is not StartupTaskState.Enabled and not StartupTaskState.EnabledByPolicy) {
                    Debug.WriteLine($"[SettingsService] StartupTask enable request did not result in 'Enabled' state. Current state: {state}");
                }
            }
            else {
                startupTask.Disable();
            }
        }
        else {
            await Task.Run(() => {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
                if (key is null) return;

                if (isEnabled) {
                    var exePath = Environment.ProcessPath;
                    if (!string.IsNullOrEmpty(exePath)) {
                        key.SetValue(AutoLaunchRegistryValueName, $"\"{exePath}\"");
                    }
                }
                else {
                    key.DeleteValue(AutoLaunchRegistryValueName, false);
                }
            });
        }
    }

    public async Task<bool> GetStartMinimizedEnabledAsync() {
        await EnsureUnpackagedSettingsLoadedAsync();
        return GetValue(StartMinimizedEnabledKey, false);
    }

    public Task SetStartMinimizedEnabledAsync(bool isEnabled) => SetValueAsync(StartMinimizedEnabledKey, isEnabled);

    public async Task<bool> GetHideToTrayEnabledAsync() {
        await EnsureUnpackagedSettingsLoadedAsync();
        return GetValue(HideToTrayEnabledKey, true);
    }

    public Task SetHideToTrayEnabledAsync(bool isEnabled) => SetValueAndNotifyAsync(HideToTrayEnabledKey, isEnabled, true, HideToTraySettingChanged);

    public async Task<bool> GetMinimizeToMiniPlayerEnabledAsync() {
        await EnsureUnpackagedSettingsLoadedAsync();
        return GetValue(MinimizeToMiniPlayerEnabledKey, false);
    }

    public Task SetMinimizeToMiniPlayerEnabledAsync(bool isEnabled) => SetValueAndNotifyAsync(MinimizeToMiniPlayerEnabledKey, isEnabled, false, MinimizeToMiniPlayerSettingChanged);

    public async Task<bool> GetShowQueueButtonEnabledAsync() {
        await EnsureUnpackagedSettingsLoadedAsync();
        return GetValue(ShowQueueButtonEnabledKey, true);
    }

    public Task SetShowQueueButtonEnabledAsync(bool isEnabled) => SetValueAndNotifyAsync(ShowQueueButtonEnabledKey, isEnabled, true, ShowQueueButtonSettingChanged);

    public async Task<bool> GetShowCoverArtInTrayFlyoutAsync() {
        await EnsureUnpackagedSettingsLoadedAsync();
        return GetValue(ShowCoverArtInTrayFlyoutKey, true);
    }

    public Task SetShowCoverArtInTrayFlyoutAsync(bool isEnabled) => SetValueAndNotifyAsync(ShowCoverArtInTrayFlyoutKey, isEnabled, true, ShowCoverArtInTrayFlyoutSettingChanged);

    public async Task<bool> GetCheckForUpdatesEnabledAsync() {
#if MSIX_PACKAGE
        return false;
#else
        await EnsureUnpackagedSettingsLoadedAsync();
        return GetValue(CheckForUpdatesEnabledKey, true);
#endif
    }

    public Task SetCheckForUpdatesEnabledAsync(bool isEnabled) {
#if MSIX_PACKAGE
        return Task.CompletedTask;
#else
        return SetValueAsync(CheckForUpdatesEnabledKey, isEnabled);
#endif
    }

    public async Task<string?> GetLastSkippedUpdateVersionAsync() {
#if MSIX_PACKAGE
        return null;
#else
        await EnsureUnpackagedSettingsLoadedAsync();
        return GetValue<string?>(LastSkippedUpdateVersionKey, null);
#endif
    }

    public Task SetLastSkippedUpdateVersionAsync(string? version) {
#if MSIX_PACKAGE
        return Task.CompletedTask;
#else
        return SetValueAsync(LastSkippedUpdateVersionKey, version);
#endif
    }

    public async Task<List<NavigationItemSetting>> GetNavigationItemsAsync() {
        var items = await GetComplexValueAsync<List<NavigationItemSetting>>(NavigationItemsKey);

        if (items != null) {
            var defaultItems = GetDefaultNavigationItems();
            var loadedTags = new HashSet<string>(items.Select(i => i.Tag));
            var missingItems = defaultItems.Where(d => !loadedTags.Contains(d.Tag));
            items.AddRange(missingItems);
            return items;
        }

        return GetDefaultNavigationItems();
    }

    public async Task SetNavigationItemsAsync(List<NavigationItemSetting> items) {
        await SetValueAsync(NavigationItemsKey, items);
        NavigationSettingsChanged?.Invoke();
    }

    public async Task<List<PlayerButtonSetting>> GetPlayerButtonSettingsAsync() {
        var settings = await GetComplexValueAsync<List<PlayerButtonSetting>>(PlayerButtonSettingsKey);

        if (settings != null) {
            // For backward compatibility, ensure the separator exists for users with older settings.
            if (!settings.Any(b => b.Id == "Separator")) {
                settings.Insert(5, new PlayerButtonSetting { Id = "Separator", DisplayName = "Layout Divider", IconGlyph = "\uE7A3", IsEnabled = true });
            }
            return settings;
        }

        return GetDefaultPlayerButtonSettings();
    }

    public async Task SetPlayerButtonSettingsAsync(List<PlayerButtonSetting> settings) {
        await SetValueAsync(PlayerButtonSettingsKey, settings);
        PlayerButtonSettingsChanged?.Invoke();
    }

    #endregion
}