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
/// This implementation supports both packaged (MSIX) and unpackaged application deployments,
/// storing settings in the appropriate location for each.
/// </summary>
public class SettingsService : ISettingsService {
    private const string AppName = "Nagi";
    private const string AutoLaunchRegistryValueName = AppName;
    private const string StartupTaskId = "NagiAutolaunchStartup";

    // Keys for various settings
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

    // Semaphore to prevent race conditions when accessing the settings file in unpackaged mode.
    private readonly SemaphoreSlim _settingsFileLock = new(1, 1);
    private static readonly JsonSerializerOptions _serializerOptions = new() { WriteIndented = true };

    /// <inheritdoc />
    public event Action<bool>? PlayerAnimationSettingChanged;
    /// <inheritdoc />
    public event Action<bool>? HideToTraySettingChanged;
    /// <inheritdoc />
    public event Action<bool>? ShowCoverArtInTrayFlyoutSettingChanged;
    /// <inheritdoc />
    public event Action? NavigationSettingsChanged;
    /// <inheritdoc />
    public event Action? LastFmSettingsChanged;
    /// <inheritdoc />
    public event Action<bool>? DiscordRichPresenceSettingChanged;

    public SettingsService(PathConfiguration pathConfig, ICredentialLockerService credentialLockerService) {
        _pathConfig = pathConfig ?? throw new ArgumentNullException(nameof(pathConfig));
        _credentialLockerService = credentialLockerService ?? throw new ArgumentNullException(nameof(credentialLockerService));
        _isPackaged = pathConfig.IsPackaged;
        _settings = new();

        if (_isPackaged) {
            _localSettings = ApplicationData.Current.LocalSettings;
        }
    }

    /// <summary>
    /// Ensures that settings are loaded from the JSON file for unpackaged applications.
    /// This method is thread-safe and performs a one-time load.
    /// </summary>
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
            Debug.WriteLine($"[SettingsService] Failed to read settings file. A new one will be created. Error: {ex.Message}");
            _settings = new Dictionary<string, object?>();
            _isInitialized = true;
        }
        finally {
            _settingsFileLock.Release();
        }
    }

    /// <summary>
    /// Retrieves a simple value from settings, providing a default if not found.
    /// </summary>
    private T GetValue<T>(string key, T defaultValue) {
        if (_isPackaged) {
            return _localSettings!.Values.TryGetValue(key, out object? value) && value is T v ? v : defaultValue;
        }
        else {
            if (_settings.TryGetValue(key, out object? value) && value != null) {
                try {
                    // Unpackaged settings are stored as JsonElements after deserialization.
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

    /// <summary>
    /// Retrieves a complex object (stored as JSON) from settings.
    /// </summary>
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
            catch (JsonException ex) {
                Debug.WriteLine($"[SettingsService] Failed to deserialize complex value for key '{key}'. Error: {ex.Message}");
                return null;
            }
        }

        return null;
    }

    /// <summary>
    /// Retrieves an enum value from settings.
    /// </summary>
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

    /// <summary>
    /// Saves a value to settings. For unpackaged apps, this rewrites the entire settings file.
    /// </summary>
    private async Task SetValueAsync<T>(string key, T value) {
        if (_isPackaged) {
            // For complex types (not strings), serialize to JSON before saving.
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

    /// <summary>
    /// Saves a value and invokes a notifier event if the value has changed.
    /// </summary>
    private async Task SetValueAndNotifyAsync<T>(string key, T newValue, T defaultValue, Action<T>? notifier) {
        await EnsureUnpackagedSettingsLoadedAsync();
        T currentValue = GetValue(key, defaultValue);

        if (!EqualityComparer<T>.Default.Equals(currentValue, newValue)) {
            notifier?.Invoke(newValue);
        }
        await SetValueAsync(key, newValue);
    }

    /// <summary>
    /// Provides the default set of navigation items for the main menu.
    /// </summary>
    private List<NavigationItemSetting> GetDefaultNavigationItems() => new()
    {
        new() { DisplayName = "Library", Tag = "library", IconGlyph = "\uE1D3", IsEnabled = true },
        new() { DisplayName = "Folders", Tag = "folders", IconGlyph = "\uE8B7", IsEnabled = true },
        new() { DisplayName = "Playlists", Tag = "playlists", IconGlyph = "\uE90B", IsEnabled = true },
        new() { DisplayName = "Artists", Tag = "artists", IconGlyph = "\uE77B", IsEnabled = true },
        new() { DisplayName = "Albums", Tag = "albums", IconGlyph = "\uE93C", IsEnabled = true },
        new() { DisplayName = "Genres", Tag = "genres", IconGlyph = "\uE8EC", IsEnabled = true }
    };

    /// <inheritdoc />
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

        // Explicitly set each setting to its default value. This ensures any
        // logic or events tied to the setters are correctly triggered.
        await ClearPlaybackStateAsync();
        await SetAutoLaunchEnabledAsync(false);
        await SetPlayerAnimationEnabledAsync(true);
        await SetHideToTrayEnabledAsync(true);
        await SetShowCoverArtInTrayFlyoutAsync(true);
        await SetFetchOnlineMetadataEnabledAsync(false);
        await SetDiscordRichPresenceEnabledAsync(false);
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

        Debug.WriteLine("[SettingsService] All application settings have been reset to their default values.");
    }

    /// <inheritdoc />
    public async Task<double> GetInitialVolumeAsync() {
        await EnsureUnpackagedSettingsLoadedAsync();
        return Math.Clamp(GetValue(VolumeKey, 0.5), 0.0, 1.0);
    }

    /// <inheritdoc />
    public Task SaveVolumeAsync(double volume) => SetValueAsync(VolumeKey, Math.Clamp(volume, 0.0, 1.0));

    /// <inheritdoc />
    public async Task<bool> GetInitialMuteStateAsync() {
        await EnsureUnpackagedSettingsLoadedAsync();
        return GetValue(MuteStateKey, false);
    }

    /// <inheritdoc />
    public Task SaveMuteStateAsync(bool isMuted) => SetValueAsync(MuteStateKey, isMuted);

    /// <inheritdoc />
    public async Task<bool> GetInitialShuffleStateAsync() {
        await EnsureUnpackagedSettingsLoadedAsync();
        return GetValue(ShuffleStateKey, false);
    }

    /// <inheritdoc />
    public Task SaveShuffleStateAsync(bool isEnabled) => SetValueAsync(ShuffleStateKey, isEnabled);

    /// <inheritdoc />
    public async Task<RepeatMode> GetInitialRepeatModeAsync() {
        await EnsureUnpackagedSettingsLoadedAsync();
        return GetEnumValue(RepeatModeKey, RepeatMode.Off);
    }

    /// <inheritdoc />
    public Task SaveRepeatModeAsync(RepeatMode mode) => SetValueAsync(RepeatModeKey, mode.ToString());

    /// <inheritdoc />
    public async Task<ElementTheme> GetThemeAsync() {
        await EnsureUnpackagedSettingsLoadedAsync();
        return GetEnumValue(ThemeKey, ElementTheme.Default);
    }

    /// <inheritdoc />
    public Task SetThemeAsync(ElementTheme theme) => SetValueAsync(ThemeKey, theme.ToString());

    /// <inheritdoc />
    public async Task<bool> GetDynamicThemingAsync() {
        await EnsureUnpackagedSettingsLoadedAsync();
        return GetValue(DynamicThemingKey, true);
    }

    /// <inheritdoc />
    public Task SetDynamicThemingAsync(bool isEnabled) => SetValueAsync(DynamicThemingKey, isEnabled);

    /// <inheritdoc />
    public async Task<bool> GetPlayerAnimationEnabledAsync() {
        await EnsureUnpackagedSettingsLoadedAsync();
        return GetValue(PlayerAnimationEnabledKey, true);
    }

    /// <inheritdoc />
    public Task SetPlayerAnimationEnabledAsync(bool isEnabled) => SetValueAndNotifyAsync(PlayerAnimationEnabledKey, isEnabled, true, PlayerAnimationSettingChanged);

    /// <inheritdoc />
    public async Task<bool> GetRestorePlaybackStateEnabledAsync() {
        await EnsureUnpackagedSettingsLoadedAsync();
        return GetValue(RestorePlaybackStateEnabledKey, true);
    }

    /// <inheritdoc />
    public Task SetRestorePlaybackStateEnabledAsync(bool isEnabled) => SetValueAsync(RestorePlaybackStateEnabledKey, isEnabled);

    /// <inheritdoc />
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

    /// <inheritdoc />
    public async Task SetAutoLaunchEnabledAsync(bool isEnabled) {
        if (_isPackaged) {
            var startupTask = await StartupTask.GetAsync(StartupTaskId);
            if (isEnabled) {
                var state = await startupTask.RequestEnableAsync();
                Debug.WriteLine($"[SettingsService] StartupTask enable request returned: {state}");
            }
            else {
                startupTask.Disable();
                Debug.WriteLine("[SettingsService] StartupTask disabled.");
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

    /// <inheritdoc />
    public async Task<bool> GetStartMinimizedEnabledAsync() {
        await EnsureUnpackagedSettingsLoadedAsync();
        return GetValue(StartMinimizedEnabledKey, false);
    }

    /// <inheritdoc />
    public Task SetStartMinimizedEnabledAsync(bool isEnabled) => SetValueAsync(StartMinimizedEnabledKey, isEnabled);

    /// <inheritdoc />
    public async Task<bool> GetHideToTrayEnabledAsync() {
        await EnsureUnpackagedSettingsLoadedAsync();
        return GetValue(HideToTrayEnabledKey, true);
    }

    /// <inheritdoc />
    public Task SetHideToTrayEnabledAsync(bool isEnabled) => SetValueAndNotifyAsync(HideToTrayEnabledKey, isEnabled, true, HideToTraySettingChanged);

    /// <inheritdoc />
    public async Task<bool> GetShowCoverArtInTrayFlyoutAsync() {
        await EnsureUnpackagedSettingsLoadedAsync();
        return GetValue(ShowCoverArtInTrayFlyoutKey, true);
    }

    /// <inheritdoc />
    public Task SetShowCoverArtInTrayFlyoutAsync(bool isEnabled) => SetValueAndNotifyAsync(ShowCoverArtInTrayFlyoutKey, isEnabled, true, ShowCoverArtInTrayFlyoutSettingChanged);

    /// <inheritdoc />
    public async Task<bool> GetFetchOnlineMetadataEnabledAsync() {
        await EnsureUnpackagedSettingsLoadedAsync();
        return GetValue(FetchOnlineMetadataKey, false);
    }

    /// <inheritdoc />
    public Task SetFetchOnlineMetadataEnabledAsync(bool isEnabled) => SetValueAsync(FetchOnlineMetadataKey, isEnabled);

    /// <inheritdoc />
    public async Task<bool> GetDiscordRichPresenceEnabledAsync() {
        await EnsureUnpackagedSettingsLoadedAsync();
        return GetValue(DiscordRichPresenceEnabledKey, false);
    }

    /// <inheritdoc />
    public Task SetDiscordRichPresenceEnabledAsync(bool isEnabled) => SetValueAndNotifyAsync(DiscordRichPresenceEnabledKey, isEnabled, true, DiscordRichPresenceSettingChanged);

    /// <inheritdoc />
    public async Task<bool> GetCheckForUpdatesEnabledAsync() {
        await EnsureUnpackagedSettingsLoadedAsync();
        return GetValue(CheckForUpdatesEnabledKey, true);
    }

    /// <inheritdoc />
    public Task SetCheckForUpdatesEnabledAsync(bool isEnabled) => SetValueAsync(CheckForUpdatesEnabledKey, isEnabled);

    /// <inheritdoc />
    public async Task<string?> GetLastSkippedUpdateVersionAsync() {
        await EnsureUnpackagedSettingsLoadedAsync();
        return GetValue<string?>(LastSkippedUpdateVersionKey, null);
    }

    /// <inheritdoc />
    public Task SetLastSkippedUpdateVersionAsync(string? version) => SetValueAsync(LastSkippedUpdateVersionKey, version);

    /// <inheritdoc />
    public async Task<List<NavigationItemSetting>> GetNavigationItemsAsync() {
        var items = await GetComplexValueAsync<List<NavigationItemSetting>>(NavigationItemsKey);

        if (items != null) {
            // This logic ensures that if new default navigation items are added in an app update,
            // they are automatically added to the user's existing settings.
            var defaultItems = GetDefaultNavigationItems();
            var loadedTags = new HashSet<string>(items.Select(i => i.Tag));
            var missingItems = defaultItems.Where(d => !loadedTags.Contains(d.Tag));
            items.AddRange(missingItems);
            return items;
        }

        return GetDefaultNavigationItems();
    }

    /// <inheritdoc />
    public async Task SetNavigationItemsAsync(List<NavigationItemSetting> items) {
        await SetValueAsync(NavigationItemsKey, items);
        NavigationSettingsChanged?.Invoke();
    }

    /// <inheritdoc />
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

    /// <inheritdoc />
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
            Debug.WriteLine($"[SettingsService] Error reading PlaybackState: {ex.Message}");
            await ClearPlaybackStateAsync();
            return null;
        }
    }

    /// <inheritdoc />
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
            Debug.WriteLine($"[SettingsService] Error clearing PlaybackState file: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public async Task<bool> GetLastFmScrobblingEnabledAsync() {
        await EnsureUnpackagedSettingsLoadedAsync();
        return GetValue(LastFmScrobblingEnabledKey, false);
    }

    /// <inheritdoc />
    public async Task SetLastFmScrobblingEnabledAsync(bool isEnabled) {
        await SetValueAsync(LastFmScrobblingEnabledKey, isEnabled);
        LastFmSettingsChanged?.Invoke();
    }

    /// <inheritdoc />
    public async Task<bool> GetLastFmNowPlayingEnabledAsync() {
        await EnsureUnpackagedSettingsLoadedAsync();
        return GetValue(LastFmNowPlayingEnabledKey, false);
    }

    /// <inheritdoc />
    public async Task SetLastFmNowPlayingEnabledAsync(bool isEnabled) {
        await SetValueAsync(LastFmNowPlayingEnabledKey, isEnabled);
        LastFmSettingsChanged?.Invoke();
    }

    /// <inheritdoc />
    public Task<(string? Username, string? SessionKey)?> GetLastFmCredentialsAsync() {
        var credentials = _credentialLockerService.RetrieveCredential(LastFmCredentialResource);
        return Task.FromResult(credentials);
    }

    /// <inheritdoc />
    public Task SaveLastFmCredentialsAsync(string username, string sessionKey) {
        _credentialLockerService.SaveCredential(LastFmCredentialResource, username, sessionKey);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task ClearLastFmCredentialsAsync() {
        _credentialLockerService.RemoveCredential(LastFmCredentialResource);
        await SetLastFmScrobblingEnabledAsync(false);
        await SetLastFmNowPlayingEnabledAsync(false);
        await SaveLastFmAuthTokenAsync(null);
    }

    /// <inheritdoc />
    public Task SaveLastFmAuthTokenAsync(string? token) {
        return SetValueAsync(LastFmAuthTokenKey, token);
    }

    /// <inheritdoc />
    public async Task<string?> GetLastFmAuthTokenAsync() {
        await EnsureUnpackagedSettingsLoadedAsync();
        return GetValue<string?>(LastFmAuthTokenKey, null);
    }
}