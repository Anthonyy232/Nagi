using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Windows.Storage;
using Microsoft.UI.Xaml;
using Nagi.Models;

namespace Nagi.Services;

/// <summary>
///     Manages application settings, persisting them to local storage.
/// </summary>
public class SettingsService : ISettingsService
{
    private const string PlaybackStateFileName = "playback_state.json";

    // Storage Keys
    private const string VolumeKey = "AppVolume";
    private const string MuteStateKey = "AppMuteState";
    private const string ShuffleStateKey = "MusicShuffleState";
    private const string RepeatModeKey = "MusicRepeatMode";
    private const string ThemeKey = "AppTheme";
    private const string DynamicThemingKey = "DynamicThemingEnabled";
    private const string PlayerAnimationEnabledKey = "PlayerAnimationEnabled";
    private const string AppPrimaryColorKey = "AppPrimaryColor";
    private static readonly JsonSerializerOptions _serializerOptions = new() { WriteIndented = false };
    private readonly StorageFolder _localFolder = ApplicationData.Current.LocalFolder;

    private readonly ApplicationDataContainer _localSettings = ApplicationData.Current.LocalSettings;
    public event Action<bool>? PlayerAnimationSettingChanged;

    public async Task ResetToDefaultsAsync()
    {
        _localSettings.Values.Clear();
        await ClearPlaybackStateAsync();
        Debug.WriteLine("[SettingsService] All application settings have been reset to their default values.");
    }

    private async Task TryDeleteStateFileAsync()
    {
        try
        {
            var item = await _localFolder.TryGetItemAsync(PlaybackStateFileName);
            if (item != null) await item.DeleteAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine(
                $"[SettingsService] Failed attempt to delete state file '{PlaybackStateFileName}': {ex.Message}");
        }
    }

    // The 'Get' methods are fast enough and don't need to be offloaded.

    #region Getters

    public Task<double> GetInitialVolumeAsync()
    {
        if (_localSettings.Values.TryGetValue(VolumeKey, out var value) && value is double volume)
            return Task.FromResult(Math.Clamp(volume, 0.0, 1.0));
        return Task.FromResult(0.5);
    }

    public Task<bool> GetInitialMuteStateAsync()
    {
        if (_localSettings.Values.TryGetValue(MuteStateKey, out var value) && value is bool isMuted)
            return Task.FromResult(isMuted);
        return Task.FromResult(false);
    }

    public Task<bool> GetInitialShuffleStateAsync()
    {
        if (_localSettings.Values.TryGetValue(ShuffleStateKey, out var value) && value is bool isEnabled)
            return Task.FromResult(isEnabled);
        return Task.FromResult(false);
    }

    public Task<RepeatMode> GetInitialRepeatModeAsync()
    {
        if (_localSettings.Values.TryGetValue(RepeatModeKey, out var value) &&
            value is string modeName &&
            Enum.TryParse<RepeatMode>(modeName, out var mode))
            return Task.FromResult(mode);
        return Task.FromResult(RepeatMode.Off);
    }

    public Task<ElementTheme> GetThemeAsync()
    {
        if (_localSettings.Values.TryGetValue(ThemeKey, out var value) &&
            value is string themeName &&
            Enum.TryParse<ElementTheme>(themeName, out var theme))
            return Task.FromResult(theme);
        return Task.FromResult(ElementTheme.Default);
    }

    public Task<bool> GetDynamicThemingAsync()
    {
        if (_localSettings.Values.TryGetValue(DynamicThemingKey, out var value) && value is bool isEnabled)
            return Task.FromResult(isEnabled);
        return Task.FromResult(true);
    }

    public Task<bool> GetPlayerAnimationEnabledAsync()
    {
        if (_localSettings.Values.TryGetValue(PlayerAnimationEnabledKey, out var value) && value is bool isEnabled)
            return Task.FromResult(isEnabled);
        return Task.FromResult(true);
    }

    #endregion

    // The 'Set' and 'Save' methods now offload their synchronous I/O to a background thread.

    #region Setters

    public Task SaveVolumeAsync(double volume)
    {
        return Task.Run(() => _localSettings.Values[VolumeKey] = Math.Clamp(volume, 0.0, 1.0));
    }

    public Task SaveMuteStateAsync(bool isMuted)
    {
        return Task.Run(() => _localSettings.Values[MuteStateKey] = isMuted);
    }

    public Task SaveShuffleStateAsync(bool isEnabled)
    {
        return Task.Run(() => _localSettings.Values[ShuffleStateKey] = isEnabled);
    }

    public Task SaveRepeatModeAsync(RepeatMode mode)
    {
        return Task.Run(() => _localSettings.Values[RepeatModeKey] = mode.ToString());
    }

    public Task SetThemeAsync(ElementTheme theme)
    {
        return Task.Run(() => _localSettings.Values[ThemeKey] = theme.ToString());
    }

    public Task SetDynamicThemingAsync(bool isEnabled)
    {
        return Task.Run(() => _localSettings.Values[DynamicThemingKey] = isEnabled);
    }

    public Task SetPlayerAnimationEnabledAsync(bool isEnabled)
    {
        // The event must be invoked on the original (UI) thread before offloading the save operation.
        PlayerAnimationSettingChanged?.Invoke(isEnabled);
        return Task.Run(() => _localSettings.Values[PlayerAnimationEnabledKey] = isEnabled);
    }

    #endregion

    #region Playback State Management

    public async Task SavePlaybackStateAsync(PlaybackState? state)
    {
        if (state == null)
        {
            await ClearPlaybackStateAsync();
            return;
        }

        try
        {
            var stateFile =
                await _localFolder.CreateFileAsync(PlaybackStateFileName, CreationCollisionOption.ReplaceExisting);
            var jsonState = JsonSerializer.Serialize(state, _serializerOptions);
            await FileIO.WriteTextAsync(stateFile, jsonState);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SettingsService] Error saving PlaybackState to file: {ex.Message}");
            await TryDeleteStateFileAsync();
        }
    }

    public async Task<PlaybackState?> GetPlaybackStateAsync()
    {
        try
        {
            var stateFile = await _localFolder.GetFileAsync(PlaybackStateFileName);
            var jsonState = await FileIO.ReadTextAsync(stateFile);

            if (string.IsNullOrEmpty(jsonState)) return null;

            return JsonSerializer.Deserialize<PlaybackState>(jsonState);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (JsonException ex)
        {
            Debug.WriteLine($"[SettingsService] Error deserializing PlaybackState (file may be corrupt): {ex.Message}");
            await TryDeleteStateFileAsync();
            return null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SettingsService] Error reading PlaybackState from file: {ex.Message}");
            return null;
        }
    }

    public async Task ClearPlaybackStateAsync()
    {
        try
        {
            var item = await _localFolder.TryGetItemAsync(PlaybackStateFileName);
            if (item != null) await item.DeleteAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SettingsService] Error clearing PlaybackState file: {ex.Message}");
        }
    }

    #endregion
}