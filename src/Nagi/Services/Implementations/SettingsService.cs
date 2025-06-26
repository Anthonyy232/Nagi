using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.Storage;
using Microsoft.UI.Xaml;
using Nagi.Models;
using Nagi.Services.Abstractions;

namespace Nagi.Services.Implementations;

/// <summary>
/// Manages application settings by persisting them to local storage.
/// This class implements the <see cref="ISettingsService"/> interface.
/// </summary>
public class SettingsService : ISettingsService {
    private const string PlaybackStateFileName = "playback_state.json";
    private const string StartupTaskId = "NagiAutolaunchStartup"; // Must match the TaskId in Package.appxmanifest

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

    private static readonly JsonSerializerOptions _serializerOptions = new() { WriteIndented = false };
    private readonly ApplicationDataContainer _localSettings = ApplicationData.Current.LocalSettings;
    private readonly StorageFolder _localFolder = ApplicationData.Current.LocalFolder;

    /// <inheritdoc/>
    public event Action<bool>? PlayerAnimationSettingChanged;
    /// <inheritdoc/>
    public event Action<bool>? HideToTraySettingChanged;

    /// <inheritdoc/>
    public async Task ResetToDefaultsAsync() {
        _localSettings.Values.Clear();
        await ClearPlaybackStateAsync();

        // Disable the startup task on reset
        await SetAutoLaunchEnabledAsync(false);

        // Notify subscribers
        PlayerAnimationSettingChanged?.Invoke(true); // Default: true
        HideToTraySettingChanged?.Invoke(true); // Default: true

        Debug.WriteLine("[SettingsService] All application settings have been reset to their default values.");
    }

    private async Task TryDeleteStateFileAsync() {
        try {
            var item = await _localFolder.TryGetItemAsync(PlaybackStateFileName);
            if (item != null) await item.DeleteAsync();
        }
        catch (Exception ex) {
            Debug.WriteLine($"[SettingsService] Failed attempt to delete state file '{PlaybackStateFileName}': {ex.Message}");
        }
    }

    private T GetValue<T>(string key, T defaultValue) {
        return _localSettings.Values.TryGetValue(key, out var value) && value is T v ? v : defaultValue;
    }

    private TEnum GetEnumValue<TEnum>(string key, TEnum defaultValue) where TEnum : struct, Enum {
        return _localSettings.Values.TryGetValue(key, out var value) &&
               value is string name &&
               Enum.TryParse<TEnum>(name, out var result)
            ? result
            : defaultValue;
    }

    private Task SetValueAsync<T>(string key, T value) {
        return Task.Run(() => _localSettings.Values[key] = value);
    }

    private Task SetValueAndNotifyAsync<T>(string key, T newValue, T defaultValue, Action<T>? notifier) {
        T currentValue = GetValue(key, defaultValue);

        if (!EqualityComparer<T>.Default.Equals(currentValue, newValue)) {
            notifier?.Invoke(newValue);
        }

        return SetValueAsync(key, newValue);
    }

    public Task<double> GetInitialVolumeAsync() => Task.FromResult(Math.Clamp(GetValue(VolumeKey, 0.5), 0.0, 1.0));
    public Task SaveVolumeAsync(double volume) => SetValueAsync(VolumeKey, Math.Clamp(volume, 0.0, 1.0));

    public Task<bool> GetInitialMuteStateAsync() => Task.FromResult(GetValue(MuteStateKey, false));
    public Task SaveMuteStateAsync(bool isMuted) => SetValueAsync(MuteStateKey, isMuted);

    public Task<bool> GetInitialShuffleStateAsync() => Task.FromResult(GetValue(ShuffleStateKey, false));
    public Task SaveShuffleStateAsync(bool isEnabled) => SetValueAsync(ShuffleStateKey, isEnabled);

    public Task<RepeatMode> GetInitialRepeatModeAsync() => Task.FromResult(GetEnumValue(RepeatModeKey, RepeatMode.Off));
    public Task SaveRepeatModeAsync(RepeatMode mode) => SetValueAsync(RepeatModeKey, mode.ToString());

    public Task<ElementTheme> GetThemeAsync() => Task.FromResult(GetEnumValue(ThemeKey, ElementTheme.Default));
    public Task SetThemeAsync(ElementTheme theme) => SetValueAsync(ThemeKey, theme.ToString());

    public Task<bool> GetDynamicThemingAsync() => Task.FromResult(GetValue(DynamicThemingKey, true));
    public Task SetDynamicThemingAsync(bool isEnabled) => SetValueAsync(DynamicThemingKey, isEnabled);

    public Task<bool> GetPlayerAnimationEnabledAsync() => Task.FromResult(GetValue(PlayerAnimationEnabledKey, true));
    public Task SetPlayerAnimationEnabledAsync(bool isEnabled) => SetValueAndNotifyAsync(PlayerAnimationEnabledKey, isEnabled, true, PlayerAnimationSettingChanged);

    public Task<bool> GetRestorePlaybackStateEnabledAsync() => Task.FromResult(GetValue(RestorePlaybackStateEnabledKey, true));
    public Task SetRestorePlaybackStateEnabledAsync(bool isEnabled) => SetValueAsync(RestorePlaybackStateEnabledKey, isEnabled);

    /// <inheritdoc/>
    public async Task<bool> GetAutoLaunchEnabledAsync() {
        StartupTask startupTask = await StartupTask.GetAsync(StartupTaskId);
        // The source of truth is the OS setting.
        return startupTask.State is StartupTaskState.Enabled;
    }

    /// <inheritdoc/>
    public async Task SetAutoLaunchEnabledAsync(bool isEnabled) {
        StartupTask startupTask = await StartupTask.GetAsync(StartupTaskId);
        if (isEnabled) {
            // Request to enable the startup task. This may pop a dialog for the user the first time.
            // The result tells us the new state, which could be 'DisabledByUser' if they deny it.
            StartupTaskState newState = await startupTask.RequestEnableAsync();
            Debug.WriteLine($"[SettingsService] Startup task enable requested. New state: {newState}");
        }
        else {
            // Disabling the startup task does not require user interaction.
            startupTask.Disable();
            Debug.WriteLine("[SettingsService] Startup task disabled.");
        }
    }

    public Task<bool> GetStartMinimizedEnabledAsync() => Task.FromResult(GetValue(StartMinimizedEnabledKey, false));
    public Task SetStartMinimizedEnabledAsync(bool isEnabled) => SetValueAsync(StartMinimizedEnabledKey, isEnabled);

    public Task<bool> GetHideToTrayEnabledAsync() => Task.FromResult(GetValue(HideToTrayEnabledKey, true));
    public Task SetHideToTrayEnabledAsync(bool isEnabled) => SetValueAndNotifyAsync(HideToTrayEnabledKey, isEnabled, false, HideToTraySettingChanged);

    /// <inheritdoc/>
    public async Task SavePlaybackStateAsync(PlaybackState? state) {
        if (state == null) {
            await ClearPlaybackStateAsync();
            return;
        }

        try {
            StorageFile stateFile = await _localFolder.CreateFileAsync(PlaybackStateFileName, CreationCollisionOption.ReplaceExisting);
            string jsonState = JsonSerializer.Serialize(state, _serializerOptions);
            await FileIO.WriteTextAsync(stateFile, jsonState);
        }
        catch (Exception ex) {
            Debug.WriteLine($"[SettingsService] Error saving PlaybackState to file: {ex.Message}");
            await TryDeleteStateFileAsync();
        }
    }

    /// <inheritdoc/>
    public async Task<PlaybackState?> GetPlaybackStateAsync() {
        try {
            StorageFile stateFile = await _localFolder.GetFileAsync(PlaybackStateFileName);
            string jsonState = await FileIO.ReadTextAsync(stateFile);

            if (string.IsNullOrEmpty(jsonState)) return null;

            return JsonSerializer.Deserialize<PlaybackState>(jsonState);
        }
        catch (FileNotFoundException) {
            // This is an expected case if no state has been saved yet.
            return null;
        }
        catch (JsonException ex) {
            Debug.WriteLine($"[SettingsService] Error deserializing PlaybackState (file may be corrupt): {ex.Message}");
            await TryDeleteStateFileAsync();
            return null;
        }
        catch (Exception ex) {
            Debug.WriteLine($"[SettingsService] Error reading PlaybackState from file: {ex.Message}");
            return null;
        }
    }

    /// <inheritdoc/>
    public async Task ClearPlaybackStateAsync() {
        try {
            IStorageItem? item = await _localFolder.TryGetItemAsync(PlaybackStateFileName);
            if (item != null) await item.DeleteAsync();
        }
        catch (Exception ex) {
            Debug.WriteLine($"[SettingsService] Error clearing PlaybackState file: {ex.Message}");
        }
    }
}