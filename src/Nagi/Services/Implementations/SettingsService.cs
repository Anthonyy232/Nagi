using Microsoft.UI.Xaml;
using Microsoft.Win32;
using Nagi.Helpers;
using Nagi.Models;
using Nagi.Services.Abstractions;
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

namespace Nagi.Services.Implementations;

/// <summary>
/// Manages application settings by persisting them to local storage.
/// Sensitive data is delegated to the ICredentialLockerService.
/// This implementation supports both packaged (MSIX) and unpackaged application deployments.
/// </summary>
public class SettingsService : ISettingsService {
    private const string AppName = "Nagi";
    private const string AutoLaunchRegistryValueName = AppName;
    private const string StartupTaskId = "NagiAutolaunchStartup";

    private const string VolumeKey = "AppVolume";
    private const string MuteStateKey = "AppMuteState";
    private const string ShuffleStateKey = "MusicShuffleState";
    private const string RepeatModeKey = "MusicRepeatMode";
    private const string ThemeKey = "AppTheme";
    private const string DynamicThemingKey = "DynamicThemingEnabled";
    private const string PlayerAnimationEnabledKey = "PlayerAnimationEnabled";
    private const string RestorePlaybackStateEnabledKey = "RestorePlaybackStateEnabled";
    private const string StartMinimizedEnabledKey = "StartMinimizedEnabled";
    private const string HideToTrayEnabledKey = "HideToTrayEnabled";
    private const string ShowCoverArtInTrayFlyoutKey = "ShowCoverArtInTrayFlyout";
    private const string FetchOnlineMetadataKey = "FetchOnlineMetadataEnabled";
    private const string DiscordRichPresenceEnabledKey = "DiscordRichPresenceEnabled";
    private const string NavigationItemsKey = "NavigationItems";
    private const string CheckForUpdatesEnabledKey = "CheckForUpdatesEnabled";
    private const string LastSkippedUpdateVersionKey = "LastSkippedUpdateVersion";
    private const string LastFmCredentialResource = "Nagi/LastFm";
    private const string LastFmAuthTokenKey = "LastFmAuthToken";
    private const string LastFmScrobblingEnabledKey = "LastFmScrobblingEnabled";
    private const string LastFmNowPlayingEnabledKey = "LastFmNowPlayingEnabled";

    private readonly PathConfiguration _pathConfig;
    private readonly ICredentialLockerService _credentialLockerService;
    private readonly bool _isPackaged;
    private readonly ApplicationDataContainer? _localSettings;
    private Dictionary<string, object?> _settings;
    private bool _isInitialized;

    // A semaphore to prevent race conditions when reading/writing the settings file in unpackaged mode.
    private readonly SemaphoreSlim _settingsFileLock = new(1, 1);

    private static readonly JsonSerializerOptions _serializerOptions = new() { WriteIndented = true };

    public event Action<bool>? PlayerAnimationSettingChanged;
    public event Action<bool>? HideToTraySettingChanged;
    public event Action<bool>? ShowCoverArtInTrayFlyoutSettingChanged;
    public event Action? NavigationSettingsChanged;
    public event Action? LastFmSettingsChanged;

    public SettingsService(PathConfiguration pathConfig, ICredentialLockerService credentialLockerService) {
        _pathConfig = pathConfig ?? throw new ArgumentNullException(nameof(pathConfig));
        _credentialLockerService = credentialLockerService ?? throw new ArgumentNullException(nameof(credentialLockerService));
        _isPackaged = pathConfig.IsPackaged;
        _settings = new();

        if (_isPackaged) {
            _localSettings = ApplicationData.Current.LocalSettings;
        }
    }

    private async Task EnsureUnpackagedSettingsLoadedAsync() {
        if (_isPackaged || _isInitialized) return;

        await _settingsFileLock.WaitAsync();
        try {
            if (_isInitialized) return;

            if (File.Exists(_pathConfig.SettingsFilePath)) {
                string json = await File.ReadAllTextAsync(_pathConfig.SettingsFilePath);
                _settings = JsonSerializer.Deserialize<Dictionary<string, object?>>(json) ?? new();
            }
            _isInitialized = true;
        }
        catch (Exception ex) {
            Debug.WriteLine($"[ERROR] SettingsService: Failed to read settings file. A new one will be created. Error: {ex.Message}");
            _settings = new Dictionary<string, object?>();
            // Mark as initialized even on failure to avoid retrying in a loop.
            _isInitialized = true;
        }
        finally {
            _settingsFileLock.Release();
        }
    }

    private T GetValue<T>(string key, T defaultValue) {
        if (_isPackaged) {
            return _localSettings!.Values.TryGetValue(key, out object? value) && value is T v ? v : defaultValue;
        }
        else {
            if (_settings.TryGetValue(key, out object? value) && value != null) {
                try {
                    // When deserializing into object, numbers can become JsonElement.
                    if (value is JsonElement element) return element.Deserialize<T>() ?? defaultValue;
                    return (T)Convert.ChangeType(value, typeof(T));
                }
                catch {
                    return defaultValue;
                }
            }
            return defaultValue;
        }
    }

    private async Task<T?> GetComplexValueAsync<T>(string key) where T : class {
        string? json = null;

        if (_isPackaged) {
            if (_localSettings!.Values.TryGetValue(key, out object? value) && value is string jsonString) {
                json = jsonString;
            }
        }
        else {
            await EnsureUnpackagedSettingsLoadedAsync();
            if (_settings.TryGetValue(key, out object? value) && value != null) {
                json = (value as JsonElement?)?.GetRawText() ?? value as string;
            }
        }

        if (json != null) {
            try {
                return JsonSerializer.Deserialize<T>(json);
            }
            catch {
                return null;
            }
        }

        return null;
    }

    private TEnum GetEnumValue<TEnum>(string key, TEnum defaultValue) where TEnum : struct, Enum {
        string? name = null;

        if (_isPackaged) {
            if (_localSettings!.Values.TryGetValue(key, out object? value) && value is string stringValue) {
                name = stringValue;
            }
        }
        else {
            if (_settings.TryGetValue(key, out object? value) && value != null) {
                name = (value as JsonElement?)?.GetString() ?? value as string;
            }
        }

        if (name != null && Enum.TryParse(name, out TEnum result)) {
            return result;
        }

        return defaultValue;
    }

    private async Task SetValueAsync<T>(string key, T value) {
        if (_isPackaged) {
            // For complex types, serialize them before storing in LocalSettings.
            if (typeof(T).IsClass && typeof(T) != typeof(string)) {
                _localSettings!.Values[key] = JsonSerializer.Serialize(value, _serializerOptions);
            }
            else {
                _localSettings!.Values[key] = value;
            }
        }
        else {
            await EnsureUnpackagedSettingsLoadedAsync();
            _settings[key] = value;
            await _settingsFileLock.WaitAsync();
            try {
                string json = JsonSerializer.Serialize(_settings, _serializerOptions);
                await File.WriteAllTextAsync(_pathConfig.SettingsFilePath, json);
            }
            finally {
                _settingsFileLock.Release();
            }
        }
    }

    private async Task SetValueAndNotifyAsync<T>(string key, T newValue, T defaultValue, Action<T>? notifier) {
        await EnsureUnpackagedSettingsLoadedAsync();
        T currentValue = GetValue(key, defaultValue);
        if (!EqualityComparer<T>.Default.Equals(currentValue, newValue)) {
            notifier?.Invoke(newValue);
        }
        await SetValueAsync(key, newValue);
    }

    private List<NavigationItemSetting> GetDefaultNavigationItems() => new()
    {
        new() { DisplayName = "Library", Tag = "library", IconGlyph = "\uE1D3", IsEnabled = true },
        new() { DisplayName = "Folders", Tag = "folders", IconGlyph = "\uE8B7", IsEnabled = true },
        new() { DisplayName = "Playlists", Tag = "playlists", IconGlyph = "\uE90B", IsEnabled = true },
        new() { DisplayName = "Artists", Tag = "artists", IconGlyph = "\uE77B", IsEnabled = true },
        new() { DisplayName = "Albums", Tag = "albums", IconGlyph = "\uE93C", IsEnabled = true },
        new() { DisplayName = "Genres", Tag = "genres", IconGlyph = "\uE8EC", IsEnabled = true }
    };

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
        await SetHideToTrayEnabledAsync(true);
        await SetShowCoverArtInTrayFlyoutAsync(true);
        await SetFetchOnlineMetadataEnabledAsync(false);
        await SetDiscordRichPresenceEnabledAsync(true);
        await SetThemeAsync(ElementTheme.Default);
        await SetDynamicThemingAsync(true);
        await SetRestorePlaybackStateEnabledAsync(true);
        await SetStartMinimizedEnabledAsync(false);
        await SetNavigationItemsAsync(GetDefaultNavigationItems());
        await SaveVolumeAsync(0.5);
        await SaveMuteStateAsync(false);
        await SaveShuffleStateAsync(false);
        await SaveRepeatModeAsync(RepeatMode.Off);
        await SetCheckForUpdatesEnabledAsync(true);
        await SetLastSkippedUpdateVersionAsync(null);
        await ClearLastFmCredentialsAsync();

        Debug.WriteLine("[INFO] SettingsService: All application settings have been reset to their default values.");
    }

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

    public async Task<ElementTheme> GetThemeAsync() {
        await EnsureUnpackagedSettingsLoadedAsync();
        return GetEnumValue(ThemeKey, ElementTheme.Default);
    }

    public Task SetThemeAsync(ElementTheme theme) => SetValueAsync(ThemeKey, theme.ToString());

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

    public async Task<bool> GetRestorePlaybackStateEnabledAsync() {
        await EnsureUnpackagedSettingsLoadedAsync();
        return GetValue(RestorePlaybackStateEnabledKey, true);
    }

    public Task SetRestorePlaybackStateEnabledAsync(bool isEnabled) => SetValueAsync(RestorePlaybackStateEnabledKey, isEnabled);

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
                Debug.WriteLine($"[INFO] SettingsService: StartupTask enable request returned: {state}");
            }
            else {
                startupTask.Disable();
                Debug.WriteLine("[INFO] SettingsService: StartupTask disabled.");
            }
        }
        else {
            await Task.Run(() => {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
                if (key is null) return;

                if (isEnabled) {
                    string? exePath = Environment.ProcessPath;
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

    public async Task<bool> GetShowCoverArtInTrayFlyoutAsync() {
        await EnsureUnpackagedSettingsLoadedAsync();
        return GetValue(ShowCoverArtInTrayFlyoutKey, true);
    }

    public Task SetShowCoverArtInTrayFlyoutAsync(bool isEnabled) => SetValueAndNotifyAsync(ShowCoverArtInTrayFlyoutKey, isEnabled, true, ShowCoverArtInTrayFlyoutSettingChanged);

    public async Task<bool> GetFetchOnlineMetadataEnabledAsync() {
        await EnsureUnpackagedSettingsLoadedAsync();
        return GetValue(FetchOnlineMetadataKey, false);
    }

    public Task SetFetchOnlineMetadataEnabledAsync(bool isEnabled) => SetValueAsync(FetchOnlineMetadataKey, isEnabled);

    public async Task<bool> GetDiscordRichPresenceEnabledAsync() {
        await EnsureUnpackagedSettingsLoadedAsync();
        return GetValue(DiscordRichPresenceEnabledKey, true);
    }

    public Task SetDiscordRichPresenceEnabledAsync(bool isEnabled) => SetValueAsync(DiscordRichPresenceEnabledKey, isEnabled);

    public async Task<bool> GetCheckForUpdatesEnabledAsync() {
        await EnsureUnpackagedSettingsLoadedAsync();
        return GetValue(CheckForUpdatesEnabledKey, true);
    }

    public Task SetCheckForUpdatesEnabledAsync(bool isEnabled) => SetValueAsync(CheckForUpdatesEnabledKey, isEnabled);

    public async Task<string?> GetLastSkippedUpdateVersionAsync() {
        await EnsureUnpackagedSettingsLoadedAsync();
        return GetValue<string?>(LastSkippedUpdateVersionKey, null);
    }

    public Task SetLastSkippedUpdateVersionAsync(string? version) => SetValueAsync(LastSkippedUpdateVersionKey, version);

    public async Task<List<NavigationItemSetting>> GetNavigationItemsAsync() {
        var items = await GetComplexValueAsync<List<NavigationItemSetting>>(NavigationItemsKey);

        // If settings exist, ensure all default items are present in case new items were added in an app update.
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

    public async Task SavePlaybackStateAsync(PlaybackState? state) {
        if (state == null) {
            await ClearPlaybackStateAsync();
            return;
        }

        string jsonState = JsonSerializer.Serialize(state, _serializerOptions);

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
                var item = await ApplicationData.Current.LocalFolder.TryGetItemAsync(
                    Path.GetFileName(_pathConfig.PlaybackStateFilePath));
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
            Debug.WriteLine($"[ERROR] SettingsService: Error reading PlaybackState: {ex.Message}");
            await ClearPlaybackStateAsync();
            return null;
        }
    }

    public async Task ClearPlaybackStateAsync() {
        try {
            if (_isPackaged) {
                var item = await ApplicationData.Current.LocalFolder.TryGetItemAsync(
                    Path.GetFileName(_pathConfig.PlaybackStateFilePath));
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
            Debug.WriteLine($"[ERROR] SettingsService: Error clearing PlaybackState file: {ex.Message}");
        }
    }

    #region Last.fm Settings

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
        // This method is async to match the interface pattern, but the underlying call is synchronous.
        var credentials = _credentialLockerService.RetrieveCredential(LastFmCredentialResource);
        return Task.FromResult(credentials);
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

    public Task SaveLastFmAuthTokenAsync(string? token) {
        return SetValueAsync(LastFmAuthTokenKey, token);
    }

    public async Task<string?> GetLastFmAuthTokenAsync() {
        await EnsureUnpackagedSettingsLoadedAsync();
        return GetValue<string?>(LastFmAuthTokenKey, null);
    }

    #endregion
}