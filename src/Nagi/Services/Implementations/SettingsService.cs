// SettingsService.cs

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.Win32;
using Nagi.Models;
using Nagi.Services.Abstractions;
using Windows.ApplicationModel;
using Windows.Storage;

namespace Nagi.Services.Implementations;

/// <summary>
///     Manages application settings by persisting them to local storage.
///     This implementation is suitable for both packaged and unpackaged applications,
///     automatically selecting the correct storage and APIs.
/// </summary>
public class SettingsService : ISettingsService {
    // --- Constants ---
    private const string AppName = "Nagi";
    private const string SettingsFileName = "settings.json";
    private const string PlaybackStateFileName = "playback_state.json";
    private const string AutoLaunchRegistryValueName = AppName;
    private const string StartupTaskId = "NagiAutolaunchStartup"; // Must match the ID in Package.appxmanifest

    // --- Setting Keys ---
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

    // --- Fields for Unpackaged App ---
    private readonly string _unpackagedAppFolderPath;
    private readonly string _unpackagedSettingsFilePath;
    private readonly string _unpackagedPlaybackStateFilePath;
    private Dictionary<string, object?> _settings;
    private static readonly SemaphoreSlim _settingsFileLock = new(1, 1);
    private bool _isInitialized;

    // --- Fields for Packaged App ---
    private readonly ApplicationDataContainer? _localSettings;

    // --- Common Fields ---
    private static readonly JsonSerializerOptions _serializerOptions = new() { WriteIndented = true };
    private static readonly bool _isPackaged = IsRunningInPackage();

    /// <inheritdoc />
    public event Action<bool>? PlayerAnimationSettingChanged;
    /// <inheritdoc />
    public event Action<bool>? HideToTraySettingChanged;
    /// <inheritdoc />
    public event Action<bool>? ShowCoverArtInTrayFlyoutSettingChanged;

    public SettingsService() {
        if (_isPackaged) {
            // Packaged: Use the WinRT ApplicationData APIs.
            _localSettings = ApplicationData.Current.LocalSettings;
            _unpackagedAppFolderPath = string.Empty;
            _unpackagedSettingsFilePath = string.Empty;
            _unpackagedPlaybackStateFilePath = string.Empty;
            _settings = new();
        }
        else {
            // Unpackaged: Use standard .NET file IO in %LOCALAPPDATA%.
            _unpackagedAppFolderPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName);
            _unpackagedSettingsFilePath = Path.Combine(_unpackagedAppFolderPath, SettingsFileName);
            _unpackagedPlaybackStateFilePath = Path.Combine(_unpackagedAppFolderPath, PlaybackStateFileName);
            _settings = new Dictionary<string, object?>();
            _isInitialized = false;
        }
    }

    #region Core Helpers (GetValue, SetValue, etc.)

    private static bool IsRunningInPackage() {
        try { return Package.Current != null; }
        catch { return false; }
    }

    private async Task EnsureUnpackagedSettingsLoadedAsync() {
        if (_isPackaged || _isInitialized) return;

        await _settingsFileLock.WaitAsync();
        try {
            if (_isInitialized) return;

            Directory.CreateDirectory(_unpackagedAppFolderPath);
            if (File.Exists(_unpackagedSettingsFilePath)) {
                var json = await File.ReadAllTextAsync(_unpackagedSettingsFilePath);
                _settings = JsonSerializer.Deserialize<Dictionary<string, object?>>(json) ?? new();
            }
            _isInitialized = true;
        }
        catch (Exception ex) {
            Debug.WriteLine($"[SettingsService] Failed to read/parse settings file. Creating new one. Error: {ex.Message}");
            _settings = new Dictionary<string, object?>();
            _isInitialized = true; // Mark as initialized even on failure to avoid retries.
        }
        finally {
            _settingsFileLock.Release();
        }
    }

    private T GetValue<T>(string key, T defaultValue) {
        if (_isPackaged) {
            return _localSettings!.Values.TryGetValue(key, out var value) && value is T v ? v : defaultValue;
        }
        else {
            if (_settings.TryGetValue(key, out var value) && value != null) {
                try {
                    if (value is JsonElement element) return element.Deserialize<T>() ?? defaultValue;
                    return (T)Convert.ChangeType(value, typeof(T));
                }
                catch { return defaultValue; }
            }
            return defaultValue;
        }
    }

    private TEnum GetEnumValue<TEnum>(string key, TEnum defaultValue) where TEnum : struct, Enum {
        if (_isPackaged) {
            return _localSettings!.Values.TryGetValue(key, out var value) && value is string name && Enum.TryParse(name, out TEnum result)
                ? result
                : defaultValue;
        }
        else {
            if (_settings.TryGetValue(key, out var value) && value != null) {
                var name = (value as JsonElement?)?.GetString() ?? value as string;
                if (name != null && Enum.TryParse(name, out TEnum result)) return result;
            }
            return defaultValue;
        }
    }

    private async Task SetValueAsync<T>(string key, T value) {
        if (_isPackaged) {
            _localSettings!.Values[key] = value;
        }
        else {
            await EnsureUnpackagedSettingsLoadedAsync();
            _settings[key] = value;
            await _settingsFileLock.WaitAsync();
            try {
                var json = JsonSerializer.Serialize(_settings, _serializerOptions);
                await File.WriteAllTextAsync(_unpackagedSettingsFilePath, json);
            }
            finally {
                _settingsFileLock.Release();
            }
        }
    }

    private async Task SetValueAndNotifyAsync<T>(string key, T newValue, T defaultValue, Action<T>? notifier) {
        await EnsureUnpackagedSettingsLoadedAsync(); // Needed for GetValue in unpackaged case
        var currentValue = GetValue(key, defaultValue);
        if (!EqualityComparer<T>.Default.Equals(currentValue, newValue)) {
            notifier?.Invoke(newValue);
        }
        await SetValueAsync(key, newValue);
    }

    #endregion

    #region Public API Implementation

    public async Task ResetToDefaultsAsync() {
        if (_isPackaged) {
            _localSettings!.Values.Clear();
        }
        else {
            await EnsureUnpackagedSettingsLoadedAsync();
            _settings.Clear();
            if (File.Exists(_unpackagedSettingsFilePath)) File.Delete(_unpackagedSettingsFilePath);
        }

        await ClearPlaybackStateAsync();
        await SetAutoLaunchEnabledAsync(false);

        // Set all settings to their default values
        await SetPlayerAnimationEnabledAsync(true);
        await SetHideToTrayEnabledAsync(true);
        await SetShowCoverArtInTrayFlyoutAsync(true);
        await SetFetchOnlineMetadataEnabledAsync(false);
        await SetThemeAsync(ElementTheme.Default);
        await SetDynamicThemingAsync(true);
        await SetRestorePlaybackStateEnabledAsync(true);
        await SetStartMinimizedEnabledAsync(false);
        await SaveVolumeAsync(0.5);
        await SaveMuteStateAsync(false);
        await SaveShuffleStateAsync(false);
        await SaveRepeatModeAsync(RepeatMode.Off);

        Debug.WriteLine("[SettingsService] All application settings have been reset to their default values.");
    }

    // The rest of the public methods now correctly use the helpers,
    // which handle the packaged/unpackaged branching internally.

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
        else {
            return await Task.Run(() => {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", false);
                return key?.GetValue(AutoLaunchRegistryValueName) != null;
            });
        }
    }

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
                if (isEnabled) {
                    var exePath = Environment.ProcessPath;
                    if (!string.IsNullOrEmpty(exePath)) key!.SetValue(AutoLaunchRegistryValueName, $"\"{exePath}\"");
                }
                else {
                    key?.DeleteValue(AutoLaunchRegistryValueName, false);
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

    public async Task SavePlaybackStateAsync(PlaybackState? state) {
        if (state == null) {
            await ClearPlaybackStateAsync();
            return;
        }

        var jsonState = JsonSerializer.Serialize(state, _serializerOptions);

        if (_isPackaged) {
            var stateFile = await ApplicationData.Current.LocalFolder.CreateFileAsync(PlaybackStateFileName, CreationCollisionOption.ReplaceExisting);
            await FileIO.WriteTextAsync(stateFile, jsonState);
        }
        else {
            Directory.CreateDirectory(_unpackagedAppFolderPath);
            await File.WriteAllTextAsync(_unpackagedPlaybackStateFilePath, jsonState);
        }
    }

    public async Task<PlaybackState?> GetPlaybackStateAsync() {
        try {
            string? jsonState = null;
            if (_isPackaged) {
                var stateFile = await ApplicationData.Current.LocalFolder.GetFileAsync(PlaybackStateFileName);
                jsonState = await FileIO.ReadTextAsync(stateFile);
            }
            else {
                if (!File.Exists(_unpackagedPlaybackStateFilePath)) return null;
                jsonState = await File.ReadAllTextAsync(_unpackagedPlaybackStateFilePath);
            }

            if (string.IsNullOrEmpty(jsonState)) return null;
            return JsonSerializer.Deserialize<PlaybackState>(jsonState);
        }
        catch (FileNotFoundException) {
            return null; // Expected if no state is saved
        }
        catch (Exception ex) {
            Debug.WriteLine($"[SettingsService] Error reading/deserializing PlaybackState: {ex.Message}");
            await ClearPlaybackStateAsync(); // Clear potentially corrupt file
            return null;
        }
    }

    public async Task ClearPlaybackStateAsync() {
        try {
            if (_isPackaged) {
                var item = await ApplicationData.Current.LocalFolder.TryGetItemAsync(PlaybackStateFileName);
                if (item != null) await item.DeleteAsync();
            }
            else {
                if (File.Exists(_unpackagedPlaybackStateFilePath)) File.Delete(_unpackagedPlaybackStateFilePath);
            }
        }
        catch (Exception ex) {
            Debug.WriteLine($"[SettingsService] Error clearing PlaybackState file: {ex.Message}");
        }
    }

    #endregion
}