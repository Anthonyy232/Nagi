using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.Storage;
using Windows.UI.ViewManagement;
using Microsoft.Extensions.Logging;
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
///     Manages application settings by persisting them to local storage. This implementation supports
///     both packaged (MSIX) and unpackaged deployments, storing settings in the appropriate location.
///     For unpackaged deployments, file writes are debounced to improve performance.
/// </summary>
public class SettingsService : IUISettingsService
{
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
    private const string FetchOnlineLyricsEnabledKey = "FetchOnlineLyricsEnabled";
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
    private const string RememberWindowSizeEnabledKey = "RememberWindowSizeEnabled";
    private const string LastWindowSizeKey = "LastWindowSize";
    private const string RememberPaneStateEnabledKey = "RememberPaneStateEnabled";
    private const string LastPaneOpenKey = "LastPaneOpen";
    private const string VolumeNormalizationEnabledKey = "VolumeNormalizationEnabled";
    private const string AccentColorKey = "AccentColor";

    private static readonly JsonSerializerOptions _serializerOptions = new() { WriteIndented = true };
    private readonly ICredentialLockerService _credentialLockerService;
    private readonly bool _isPackaged;
    private readonly ApplicationDataContainer? _localSettings;
    private readonly ILogger<SettingsService> _logger;
    private readonly IPathConfiguration _pathConfig;
    private readonly TimeSpan _saveDebounceDelay = TimeSpan.FromMilliseconds(500);
    private readonly SemaphoreSlim _settingsFileLock = new(1, 1);
    private readonly UISettings _uiSettings = new();
    private readonly IDispatcherService _dispatcherService;
    private volatile bool _isInitialized;
    private int _isSaveQueued;

    private readonly object _dictLock = new();
    private Dictionary<string, object?> _settings;

    public SettingsService(IPathConfiguration pathConfig, ICredentialLockerService credentialLockerService,
        ILogger<SettingsService> logger, IDispatcherService dispatcherService)
    {
        _pathConfig = pathConfig ?? throw new ArgumentNullException(nameof(pathConfig));
        _credentialLockerService =
            credentialLockerService ?? throw new ArgumentNullException(nameof(credentialLockerService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _dispatcherService = dispatcherService ?? throw new ArgumentNullException(nameof(dispatcherService));
        _isPackaged = pathConfig.IsPackaged;
        _settings = new Dictionary<string, object?>();

        if (_isPackaged) _localSettings = ApplicationData.Current.LocalSettings;

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
    public event Action<bool>? TransparencyEffectsSettingChanged;
    public event Action<BackdropMaterial>? BackdropMaterialChanged;

    public bool IsTransparencyEffectsEnabled()
    {
        return _uiSettings.AdvancedEffectsEnabled;
    }

    public async Task ResetToDefaultsAsync()
    {
        if (_isPackaged)
        {
            _localSettings!.Values.Clear();
        }
        else
        {
            lock (_dictLock)
            {
                _settings.Clear();
            }
            if (File.Exists(_pathConfig.SettingsFilePath)) File.Delete(_pathConfig.SettingsFilePath);
        }

        await ClearPlaybackStateAsync();
        await SetAutoLaunchEnabledAsync(SettingsDefaults.AutoLaunchEnabled);
        await SetPlayerAnimationEnabledAsync(SettingsDefaults.PlayerAnimationEnabled);
        await SetShowQueueButtonEnabledAsync(SettingsDefaults.ShowQueueButtonEnabled);
        await SetHideToTrayEnabledAsync(SettingsDefaults.HideToTrayEnabled);
        await SetMinimizeToMiniPlayerEnabledAsync(SettingsDefaults.MinimizeToMiniPlayerEnabled);
        await SetShowCoverArtInTrayFlyoutAsync(SettingsDefaults.ShowCoverArtInTrayFlyoutEnabled);
        await SetFetchOnlineMetadataEnabledAsync(SettingsDefaults.FetchOnlineMetadataEnabled);
        await SetFetchOnlineLyricsEnabledAsync(SettingsDefaults.FetchOnlineLyricsEnabled);
        await SetDiscordRichPresenceEnabledAsync(SettingsDefaults.DiscordRichPresenceEnabled);
        await SetThemeAsync(SettingsDefaults.Theme);
        await SetBackdropMaterialAsync(SettingsDefaults.DefaultBackdropMaterial);
        await SetDynamicThemingAsync(SettingsDefaults.DynamicThemingEnabled);
        await SetRestorePlaybackStateEnabledAsync(SettingsDefaults.RestorePlaybackStateEnabled);
        await SetStartMinimizedEnabledAsync(SettingsDefaults.StartMinimizedEnabled);
        await SetNavigationItemsAsync(GetDefaultNavigationItems());
        await SetPlayerButtonSettingsAsync(GetDefaultPlayerButtonSettings());
        await SaveVolumeAsync(SettingsDefaults.Volume);
        await SaveMuteStateAsync(SettingsDefaults.MuteState);
        await SaveShuffleStateAsync(SettingsDefaults.ShuffleState);
        await SaveRepeatModeAsync(SettingsDefaults.DefaultRepeatMode);
        await SetCheckForUpdatesEnabledAsync(SettingsDefaults.CheckForUpdatesEnabled);
        await SetLastSkippedUpdateVersionAsync(null);
        await SetLastFmScrobblingEnabledAsync(SettingsDefaults.LastFmScrobblingEnabled);
        await SetLastFmNowPlayingEnabledAsync(SettingsDefaults.LastFmNowPlayingEnabled);
        await ClearLastFmCredentialsAsync();
        await SetEqualizerSettingsAsync(new EqualizerSettings());
        await SetRememberWindowSizeEnabledAsync(SettingsDefaults.RememberWindowSizeEnabled);
        await SetRememberPaneStateEnabledAsync(SettingsDefaults.RememberPaneStateEnabled);
        await SetVolumeNormalizationEnabledAsync(SettingsDefaults.VolumeNormalizationEnabled);
        await SetAccentColorAsync(SettingsDefaults.AccentColor);

        _logger.LogInformation("All application settings have been reset to their default values.");
    }

    private void OnAdvancedEffectsEnabledChanged(UISettings sender, object args)
    {
        _dispatcherService.TryEnqueue(() => TransparencyEffectsSettingChanged?.Invoke(_uiSettings.AdvancedEffectsEnabled));
    }

    /// <summary>
    ///     For unpackaged deployments, ensures the settings dictionary is loaded from the JSON file on disk.
    ///     This operation is thread-safe and will only execute once.
    /// </summary>
    private async Task EnsureUnpackagedSettingsLoadedAsync()
    {
        if (_isPackaged || _isInitialized) return;

        // Perform an early check to avoid the overhead of a lock if we're already initialized.
        // This is particularly important for parallel calls during page load.
        if (_isInitialized) return;

        await _settingsFileLock.WaitAsync();
        try
        {
            if (_isInitialized) return;

            if (File.Exists(_pathConfig.SettingsFilePath))
            {
                var json = await File.ReadAllTextAsync(_pathConfig.SettingsFilePath);
                var deserialized = JsonSerializer.Deserialize<Dictionary<string, object?>>(json);
                lock (_dictLock)
                {
                    _settings = deserialized ?? new Dictionary<string, object?>();
                }
            }

            _isInitialized = true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read settings file. A new one will be created.");
            lock (_dictLock)
            {
                _settings = new Dictionary<string, object?>();
            }
            _isInitialized = true;
        }
        finally
        {
            _settingsFileLock.Release();
        }
    }

    /// <summary>
    ///     Queues a request to save the settings to a file for unpackaged deployments.
    ///     This operation is debounced to prevent excessive I/O during rapid changes.
    /// </summary>
    private async Task QueueSaveAsync()
    {
        if (Interlocked.CompareExchange(ref _isSaveQueued, 1, 0) == 0)
        {
            await Task.Delay(_saveDebounceDelay);

            await _settingsFileLock.WaitAsync();
            try
            {
                string json;
                lock (_dictLock)
                {
                    json = JsonSerializer.Serialize(_settings, _serializerOptions);
                }
                await File.WriteAllTextAsync(_pathConfig.SettingsFilePath, json);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "CRITICAL: Failed to save settings to file.");
            }
            finally
            {
                _settingsFileLock.Release();
                Interlocked.Exchange(ref _isSaveQueued, 0);
            }
        }
    }

    private T GetValue<T>(string key, T defaultValue)
    {
        if (_isPackaged)
        {
            return _localSettings!.Values.TryGetValue(key, out var value) && value is T v ? v : defaultValue;
        }
        else
        {
            bool hasValue;
            object? value;
            lock (_dictLock)
            {
                hasValue = _settings.TryGetValue(key, out value);
            }

            if (hasValue && value != null)
                try
                {
                    if (value is JsonElement element) return element.Deserialize<T>() ?? defaultValue;
                }
                catch (JsonException)
                {
                    return defaultValue;
                }

            return defaultValue;
        }
    }

    private async Task<T?> GetComplexValueAsync<T>(string key) where T : class
    {
        string? json = null;

        if (_isPackaged)
        {
            if (_localSettings!.Values.TryGetValue(key, out var value) && value is string jsonString) json = jsonString;
        }
        else
        {
            await EnsureUnpackagedSettingsLoadedAsync();
            bool hasValue;
            object? value;
            lock (_dictLock)
            {
                hasValue = _settings.TryGetValue(key, out value);
            }

            if (hasValue && value != null)
                json = (value as JsonElement?)?.GetRawText() ?? value as string;
        }

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

        if (_isPackaged)
        {
            if (_localSettings!.Values.TryGetValue(key, out var value) && value is string stringValue)
                name = stringValue;
        }
        else
        {
            bool hasValue;
            object? value;
            lock (_dictLock)
            {
                hasValue = _settings.TryGetValue(key, out value);
            }

            if (hasValue && value is JsonElement element) name = element.GetString();
        }

        if (name != null && Enum.TryParse(name, out TEnum result)) return result;

        return defaultValue;
    }

    private async Task SetValueAsync<T>(string key, T value)
    {
        if (_isPackaged)
        {
            if (typeof(T).IsClass && typeof(T) != typeof(string))
                _localSettings!.Values[key] = JsonSerializer.Serialize(value, _serializerOptions);
            else
                _localSettings!.Values[key] = value;
        }
        else
        {
            await EnsureUnpackagedSettingsLoadedAsync();

            if (value is null)
            {
                lock (_dictLock)
                {
                    _settings[key] = null;
                }
            }
            else
            {
                var serializedValue = JsonSerializer.Serialize(value);
                var element = JsonDocument.Parse(serializedValue).RootElement.Clone();
                lock (_dictLock)
                {
                    _settings[key] = element;
                }
            }

            _ = QueueSaveAsync();
        }
    }

    private async Task SetValueAndNotifyAsync<T>(string key, T newValue, T defaultValue, Action<T>? notifier)
    {
        await EnsureUnpackagedSettingsLoadedAsync();
        var currentValue = GetValue(key, defaultValue);

        await SetValueAsync(key, newValue);
        if (!EqualityComparer<T>.Default.Equals(currentValue, newValue)) notifier?.Invoke(newValue);
    }

    private List<NavigationItemSetting> GetDefaultNavigationItems()
    {
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

    private List<PlayerButtonSetting> GetDefaultPlayerButtonSettings()
    {
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
            new() { Id = "Volume", DisplayName = "Volume", IconGlyph = "\uE767", IsEnabled = true }
        };
    }

    #region Core Settings (ISettingsService)

    public async Task<double> GetInitialVolumeAsync()
    {
        await EnsureUnpackagedSettingsLoadedAsync();
        return Math.Clamp(GetValue(VolumeKey, SettingsDefaults.Volume), 0.0, 1.0);
    }

    public Task SaveVolumeAsync(double volume)
    {
        return SetValueAsync(VolumeKey, Math.Clamp(volume, 0.0, 1.0));
    }

    public async Task<bool> GetInitialMuteStateAsync()
    {
        await EnsureUnpackagedSettingsLoadedAsync();
        return GetValue(MuteStateKey, SettingsDefaults.MuteState);
    }

    public Task SaveMuteStateAsync(bool isMuted)
    {
        return SetValueAsync(MuteStateKey, isMuted);
    }

    public async Task<bool> GetInitialShuffleStateAsync()
    {
        await EnsureUnpackagedSettingsLoadedAsync();
        return GetValue(ShuffleStateKey, SettingsDefaults.ShuffleState);
    }

    public Task SaveShuffleStateAsync(bool isEnabled)
    {
        return SetValueAsync(ShuffleStateKey, isEnabled);
    }

    public async Task<RepeatMode> GetInitialRepeatModeAsync()
    {
        await EnsureUnpackagedSettingsLoadedAsync();
        return GetEnumValue(RepeatModeKey, SettingsDefaults.DefaultRepeatMode);
    }

    public Task SaveRepeatModeAsync(RepeatMode mode)
    {
        return SetValueAsync(RepeatModeKey, mode.ToString());
    }

    public async Task<bool> GetRestorePlaybackStateEnabledAsync()
    {
        await EnsureUnpackagedSettingsLoadedAsync();
        return GetValue(RestorePlaybackStateEnabledKey, SettingsDefaults.RestorePlaybackStateEnabled);
    }

    public Task SetRestorePlaybackStateEnabledAsync(bool isEnabled)
    {
        return SetValueAsync(RestorePlaybackStateEnabledKey, isEnabled);
    }

    public async Task<bool> GetFetchOnlineMetadataEnabledAsync()
    {
        await EnsureUnpackagedSettingsLoadedAsync();
        return GetValue(FetchOnlineMetadataKey, SettingsDefaults.FetchOnlineMetadataEnabled);
    }

    public Task SetFetchOnlineMetadataEnabledAsync(bool isEnabled)
    {
        return SetValueAsync(FetchOnlineMetadataKey, isEnabled);
    }

    public async Task<bool> GetFetchOnlineLyricsEnabledAsync()
    {
        await EnsureUnpackagedSettingsLoadedAsync();
        return GetValue(FetchOnlineLyricsEnabledKey, SettingsDefaults.FetchOnlineLyricsEnabled);
    }

    public Task SetFetchOnlineLyricsEnabledAsync(bool isEnabled)
    {
        return SetValueAsync(FetchOnlineLyricsEnabledKey, isEnabled);
    }

    public async Task<bool> GetDiscordRichPresenceEnabledAsync()
    {
        await EnsureUnpackagedSettingsLoadedAsync();
        return GetValue(DiscordRichPresenceEnabledKey, SettingsDefaults.DiscordRichPresenceEnabled);
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
            await ClearPlaybackStateAsync();
            return;
        }

        var jsonState = JsonSerializer.Serialize(state, _serializerOptions);

        if (_isPackaged)
        {
            var stateFile = await ApplicationData.Current.LocalFolder.CreateFileAsync(
                Path.GetFileName(_pathConfig.PlaybackStateFilePath), CreationCollisionOption.ReplaceExisting);
            await FileIO.WriteTextAsync(stateFile, jsonState);
        }
        else
        {
            await File.WriteAllTextAsync(_pathConfig.PlaybackStateFilePath, jsonState);
        }
    }

    public async Task<PlaybackState?> GetPlaybackStateAsync()
    {
        try
        {
            string? jsonState = null;
            if (_isPackaged)
            {
                var item = await ApplicationData.Current.LocalFolder.TryGetItemAsync(
                    Path.GetFileName(_pathConfig.PlaybackStateFilePath));
                if (item is IStorageFile stateFile) jsonState = await FileIO.ReadTextAsync(stateFile);
            }
            else
            {
                if (!File.Exists(_pathConfig.PlaybackStateFilePath)) return null;
                jsonState = await File.ReadAllTextAsync(_pathConfig.PlaybackStateFilePath);
            }

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
            await ClearPlaybackStateAsync();
            return null;
        }
    }

    public async Task ClearPlaybackStateAsync()
    {
        try
        {
            if (_isPackaged)
            {
                var item = await ApplicationData.Current.LocalFolder.TryGetItemAsync(
                    Path.GetFileName(_pathConfig.PlaybackStateFilePath));
                if (item != null) await item.DeleteAsync();
            }
            else
            {
                if (File.Exists(_pathConfig.PlaybackStateFilePath)) File.Delete(_pathConfig.PlaybackStateFilePath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error clearing PlaybackState file.");
        }
    }

    public async Task<bool> GetLastFmScrobblingEnabledAsync()
    {
        await EnsureUnpackagedSettingsLoadedAsync();
        return GetValue(LastFmScrobblingEnabledKey, SettingsDefaults.LastFmScrobblingEnabled);
    }

    public async Task SetLastFmScrobblingEnabledAsync(bool isEnabled)
    {
        await SetValueAsync(LastFmScrobblingEnabledKey, isEnabled);
        LastFmSettingsChanged?.Invoke();
    }

    public async Task<bool> GetLastFmNowPlayingEnabledAsync()
    {
        await EnsureUnpackagedSettingsLoadedAsync();
        return GetValue(LastFmNowPlayingEnabledKey, SettingsDefaults.LastFmNowPlayingEnabled);
    }

    public async Task SetLastFmNowPlayingEnabledAsync(bool isEnabled)
    {
        await SetValueAsync(LastFmNowPlayingEnabledKey, isEnabled);
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
        await SetLastFmScrobblingEnabledAsync(false);
        await SetLastFmNowPlayingEnabledAsync(false);
        await SaveLastFmAuthTokenAsync(null);
    }

    public Task SaveLastFmAuthTokenAsync(string? token)
    {
        return SetValueAsync(LastFmAuthTokenKey, token);
    }

    public async Task<string?> GetLastFmAuthTokenAsync()
    {
        await EnsureUnpackagedSettingsLoadedAsync();
        return GetValue<string?>(LastFmAuthTokenKey, null);
    }

    public Task<EqualizerSettings?> GetEqualizerSettingsAsync()
    {
        return GetComplexValueAsync<EqualizerSettings>(EqualizerSettingsKey);
    }

    public Task SetEqualizerSettingsAsync(EqualizerSettings settings)
    {
        return SetValueAsync(EqualizerSettingsKey, settings);
    }

    #endregion

    #region UI Settings (IUISettingsService)

    public async Task<ElementTheme> GetThemeAsync()
    {
        await EnsureUnpackagedSettingsLoadedAsync();
        return GetEnumValue(ThemeKey, SettingsDefaults.Theme);
    }

    public Task SetThemeAsync(ElementTheme theme)
    {
        return SetValueAsync(ThemeKey, theme.ToString());
    }

    public async Task<BackdropMaterial> GetBackdropMaterialAsync()
    {
        await EnsureUnpackagedSettingsLoadedAsync();
        return GetEnumValue(BackdropMaterialKey, SettingsDefaults.DefaultBackdropMaterial);
    }

    public async Task SetBackdropMaterialAsync(BackdropMaterial material)
    {
        await SetValueAsync(BackdropMaterialKey, material.ToString());
        BackdropMaterialChanged?.Invoke(material);
    }

    public async Task<bool> GetDynamicThemingAsync()
    {
        await EnsureUnpackagedSettingsLoadedAsync();
        return GetValue(DynamicThemingKey, SettingsDefaults.DynamicThemingEnabled);
    }

    public Task SetDynamicThemingAsync(bool isEnabled)
    {
        return SetValueAsync(DynamicThemingKey, isEnabled);
    }

    public async Task<bool> GetPlayerAnimationEnabledAsync()
    {
        await EnsureUnpackagedSettingsLoadedAsync();
        return GetValue(PlayerAnimationEnabledKey, SettingsDefaults.PlayerAnimationEnabled);
    }

    public Task SetPlayerAnimationEnabledAsync(bool isEnabled)
    {
        return SetValueAndNotifyAsync(PlayerAnimationEnabledKey, isEnabled, SettingsDefaults.PlayerAnimationEnabled, PlayerAnimationSettingChanged);
    }

    public async Task<bool> GetAutoLaunchEnabledAsync()
    {
        if (_isPackaged)
        {
            try
            {
                var startupTask = await StartupTask.GetAsync(StartupTaskId);
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

        return await Task.Run(() =>
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", false);
            return key?.GetValue(AutoLaunchRegistryValueName) != null;
        });
    }

    public async Task SetAutoLaunchEnabledAsync(bool isEnabled)
    {
        if (_isPackaged)
        {
            var startupTask = await StartupTask.GetAsync(StartupTaskId);
            if (isEnabled)
            {
                var state = await startupTask.RequestEnableAsync();
                if (state is not StartupTaskState.Enabled and not StartupTaskState.EnabledByPolicy)
                    _logger.LogWarning(
                        "StartupTask enable request did not result in 'Enabled' state. Current state: {State}", state);
            }
            else
            {
                startupTask.Disable();
            }
        }
        else
        {
            await Task.Run(() =>
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
                if (key is null) return;

                if (isEnabled)
                {
                    var exePath = Environment.ProcessPath;
                    if (!string.IsNullOrEmpty(exePath)) key.SetValue(AutoLaunchRegistryValueName, $"\"{exePath}\"");
                }
                else
                {
                    key.DeleteValue(AutoLaunchRegistryValueName, false);
                }
            });
        }
    }

    public async Task<bool> GetStartMinimizedEnabledAsync()
    {
        await EnsureUnpackagedSettingsLoadedAsync();
        return GetValue(StartMinimizedEnabledKey, SettingsDefaults.StartMinimizedEnabled);
    }

    public Task SetStartMinimizedEnabledAsync(bool isEnabled)
    {
        return SetValueAsync(StartMinimizedEnabledKey, isEnabled);
    }

    public async Task<bool> GetHideToTrayEnabledAsync()
    {
        await EnsureUnpackagedSettingsLoadedAsync();
        return GetValue(HideToTrayEnabledKey, SettingsDefaults.HideToTrayEnabled);
    }

    public Task SetHideToTrayEnabledAsync(bool isEnabled)
    {
        return SetValueAndNotifyAsync(HideToTrayEnabledKey, isEnabled, SettingsDefaults.HideToTrayEnabled, HideToTraySettingChanged);
    }

    public async Task<bool> GetMinimizeToMiniPlayerEnabledAsync()
    {
        await EnsureUnpackagedSettingsLoadedAsync();
        return GetValue(MinimizeToMiniPlayerEnabledKey, SettingsDefaults.MinimizeToMiniPlayerEnabled);
    }

    public Task SetMinimizeToMiniPlayerEnabledAsync(bool isEnabled)
    {
        return SetValueAndNotifyAsync(MinimizeToMiniPlayerEnabledKey, isEnabled, SettingsDefaults.MinimizeToMiniPlayerEnabled,
            MinimizeToMiniPlayerSettingChanged);
    }

    public async Task<bool> GetShowQueueButtonEnabledAsync()
    {
        await EnsureUnpackagedSettingsLoadedAsync();
        return GetValue(ShowQueueButtonEnabledKey, SettingsDefaults.ShowQueueButtonEnabled);
    }

    public Task SetShowQueueButtonEnabledAsync(bool isEnabled)
    {
        return SetValueAndNotifyAsync(ShowQueueButtonEnabledKey, isEnabled, SettingsDefaults.ShowQueueButtonEnabled, ShowQueueButtonSettingChanged);
    }

    public async Task<bool> GetShowCoverArtInTrayFlyoutAsync()
    {
        await EnsureUnpackagedSettingsLoadedAsync();
        return GetValue(ShowCoverArtInTrayFlyoutKey, SettingsDefaults.ShowCoverArtInTrayFlyoutEnabled);
    }

    public Task SetShowCoverArtInTrayFlyoutAsync(bool isEnabled)
    {
        return SetValueAndNotifyAsync(ShowCoverArtInTrayFlyoutKey, isEnabled, SettingsDefaults.ShowCoverArtInTrayFlyoutEnabled,
            ShowCoverArtInTrayFlyoutSettingChanged);
    }

    public async Task<Windows.UI.Color?> GetAccentColorAsync()
    {
        await EnsureUnpackagedSettingsLoadedAsync();
        var hex = GetValue<string?>(AccentColorKey, null);
        if (string.IsNullOrEmpty(hex)) return null;

        if (App.CurrentApp!.TryParseHexColor(hex, out var color))
        {
            return color;
        }

        return null;
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

    public async Task<bool> GetCheckForUpdatesEnabledAsync()
    {
#if MSIX_PACKAGE
        await Task.CompletedTask;
        return false;
#else
        await EnsureUnpackagedSettingsLoadedAsync();
        return GetValue(CheckForUpdatesEnabledKey, SettingsDefaults.CheckForUpdatesEnabled);
#endif
    }

    public Task SetCheckForUpdatesEnabledAsync(bool isEnabled)
    {
#if MSIX_PACKAGE
        return Task.CompletedTask;
#else
        return SetValueAsync(CheckForUpdatesEnabledKey, isEnabled);
#endif
    }

    public async Task<string?> GetLastSkippedUpdateVersionAsync()
    {
#if MSIX_PACKAGE
        await Task.CompletedTask;
        return null;
#else
        await EnsureUnpackagedSettingsLoadedAsync();
        return GetValue<string?>(LastSkippedUpdateVersionKey, null);
#endif
    }

    public Task SetLastSkippedUpdateVersionAsync(string? version)
    {
#if MSIX_PACKAGE
        return Task.CompletedTask;
#else
        return SetValueAsync(LastSkippedUpdateVersionKey, version);
#endif
    }

    public async Task<List<NavigationItemSetting>> GetNavigationItemsAsync()
    {
        var items = await GetComplexValueAsync<List<NavigationItemSetting>>(NavigationItemsKey);

        if (items != null)
        {
            var defaultItems = GetDefaultNavigationItems();
            var loadedTags = new HashSet<string>(items.Select(i => i.Tag));
            var missingItems = defaultItems.Where(d => !loadedTags.Contains(d.Tag));
            items.AddRange(missingItems);
            return items;
        }

        return GetDefaultNavigationItems();
    }

    public async Task SetNavigationItemsAsync(List<NavigationItemSetting> items)
    {
        await SetValueAsync(NavigationItemsKey, items);
        NavigationSettingsChanged?.Invoke();
    }

    public async Task<List<PlayerButtonSetting>> GetPlayerButtonSettingsAsync()
    {
        var settings = await GetComplexValueAsync<List<PlayerButtonSetting>>(PlayerButtonSettingsKey);

        if (settings != null)
        {
            // For backward compatibility, ensure the separator exists for users with older settings.
            if (!settings.Any(b => b.Id == "Separator"))
                settings.Insert(5,
                    new PlayerButtonSetting
                        { Id = "Separator", DisplayName = "Layout Divider", IconGlyph = "\uE7A3", IsEnabled = true });
            return settings;
        }

        return GetDefaultPlayerButtonSettings();
    }

    public async Task SetPlayerButtonSettingsAsync(List<PlayerButtonSetting> settings)
    {
        await SetValueAsync(PlayerButtonSettingsKey, settings);
        PlayerButtonSettingsChanged?.Invoke();
    }

    public async Task<bool> GetRememberWindowSizeEnabledAsync()
    {
        await EnsureUnpackagedSettingsLoadedAsync();
        return GetValue(RememberWindowSizeEnabledKey, SettingsDefaults.RememberWindowSizeEnabled);
    }

    public Task SetRememberWindowSizeEnabledAsync(bool isEnabled)
    {
        return SetValueAsync(RememberWindowSizeEnabledKey, isEnabled);
    }

    public async Task<(int Width, int Height)?> GetLastWindowSizeAsync()
    {
        var sizeData = await GetComplexValueAsync<int[]>(LastWindowSizeKey);
        if (sizeData is { Length: 2 })
            return (sizeData[0], sizeData[1]);
        return null;
    }

    public Task SetLastWindowSizeAsync(int width, int height)
    {
        return SetValueAsync(LastWindowSizeKey, new[] { width, height });
    }

    public async Task<bool> GetRememberPaneStateEnabledAsync()
    {
        await EnsureUnpackagedSettingsLoadedAsync();
        return GetValue(RememberPaneStateEnabledKey, SettingsDefaults.RememberPaneStateEnabled);
    }

    public Task SetRememberPaneStateEnabledAsync(bool isEnabled)
    {
        return SetValueAsync(RememberPaneStateEnabledKey, isEnabled);
    }

    public async Task<bool?> GetLastPaneOpenAsync()
    {
        await EnsureUnpackagedSettingsLoadedAsync();
        // Use a string to differentiate between 'never set' and 'set to false'
        var value = GetValue<string?>(LastPaneOpenKey, null);
        if (value == null) return null;
        return value == "true";
    }

    public Task SetLastPaneOpenAsync(bool isOpen)
    {
        return SetValueAsync(LastPaneOpenKey, isOpen ? "true" : "false");
    }

    public async Task<bool> GetVolumeNormalizationEnabledAsync()
    {
        await EnsureUnpackagedSettingsLoadedAsync();
        return GetValue(VolumeNormalizationEnabledKey, SettingsDefaults.VolumeNormalizationEnabled);
    }

    public Task SetVolumeNormalizationEnabledAsync(bool isEnabled)
    {
        return SetValueAndNotifyAsync(VolumeNormalizationEnabledKey, isEnabled, SettingsDefaults.VolumeNormalizationEnabled,
            VolumeNormalizationEnabledChanged);
    }

    #endregion
}