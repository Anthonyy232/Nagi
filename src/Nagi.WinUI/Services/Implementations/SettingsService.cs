using System;
using System.Collections.Frozen;
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
public class SettingsService : IUISettingsService, IDisposable
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
    private const string RememberWindowPositionEnabledKey = "RememberWindowPositionEnabled";
    private const string LastWindowPositionKey = "LastWindowPosition";
    private const string RememberPaneStateEnabledKey = "RememberPaneStateEnabled";
    private const string LastPaneOpenKey = "LastPaneOpen";
    private const string VolumeNormalizationEnabledKey = "VolumeNormalizationEnabled";
    private const string AccentColorKey = "AccentColor";
    private const string LyricsServiceProvidersKey = "LyricsServiceProviders";
    private const string MetadataServiceProvidersKey = "MetadataServiceProviders";
    private const string ArtistSplitCharactersKey = "ArtistSplitCharacters";

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
    private bool _disposed;

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
    public event Action<bool>? FetchOnlineMetadataEnabledChanged;
    public event Action<bool>? FetchOnlineLyricsEnabledChanged;
    public event Action<ServiceCategory>? ServiceProvidersChanged;
    public event Action? ArtistSplitCharactersChanged;

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
            SetCheckForUpdatesEnabledAsync(SettingsDefaults.CheckForUpdatesEnabled),
            SetLastSkippedUpdateVersionAsync(null),
            SetLastFmScrobblingEnabledAsync(SettingsDefaults.LastFmScrobblingEnabled),
            SetLastFmNowPlayingEnabledAsync(SettingsDefaults.LastFmNowPlayingEnabled),
            ClearLastFmCredentialsAsync(),
            SetEqualizerSettingsAsync(new EqualizerSettings()),
            SetRememberWindowSizeEnabledAsync(SettingsDefaults.RememberWindowSizeEnabled),
            SetRememberWindowPositionEnabledAsync(SettingsDefaults.RememberWindowPositionEnabled),
            SetRememberPaneStateEnabledAsync(SettingsDefaults.RememberPaneStateEnabled),
            SetVolumeNormalizationEnabledAsync(SettingsDefaults.VolumeNormalizationEnabled),
            SetAccentColorAsync(SettingsDefaults.AccentColor),
            SetArtistSplitCharactersAsync(SettingsDefaults.DefaultArtistSplitCharacters)
        };

        await Task.WhenAll(tasks).ConfigureAwait(false);

        // Clear sort order settings
        lock (_dictLock)
        {
            var keysToRemove = _settings.Keys.Where(k => k.StartsWith("SortOrder_")).ToList();
            foreach (var key in keysToRemove) _settings.Remove(key);
        }
        if (!_isPackaged) _ = QueueSaveAsync();
        else
        {
            var keysToRemove = _localSettings!.Values.Keys.Where(k => k.StartsWith("SortOrder_")).ToList();
            foreach (var key in keysToRemove) _localSettings.Values.Remove(key);
        }

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
        // Fast path: if packaged or already initialized, return immediately without any lock overhead.
        // The volatile read of _isInitialized ensures we see the latest value across threads.
        if (_isPackaged || _isInitialized || _disposed) return;
        try
        {
            await _settingsFileLock.WaitAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return;
        }
        try
        {
            if (_isInitialized) return;

            if (File.Exists(_pathConfig.SettingsFilePath))
            {
                var json = await File.ReadAllTextAsync(_pathConfig.SettingsFilePath).ConfigureAwait(false);
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
            await Task.Delay(_saveDebounceDelay).ConfigureAwait(false);
 
            if (_disposed) return;
            try
            {
                await _settingsFileLock.WaitAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            try
            {
                string json;
                lock (_dictLock)
                {
                    json = JsonSerializer.Serialize(_settings, _serializerOptions);
                }
                await File.WriteAllTextAsync(_pathConfig.SettingsFilePath, json).ConfigureAwait(false);
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
            await EnsureUnpackagedSettingsLoadedAsync().ConfigureAwait(false);
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
            await EnsureUnpackagedSettingsLoadedAsync().ConfigureAwait(false);

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
        await EnsureUnpackagedSettingsLoadedAsync().ConfigureAwait(false);
        var currentValue = GetValue(key, defaultValue);

        await SetValueAsync(key, newValue).ConfigureAwait(false);
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

    public List<PlayerButtonSetting> GetDefaultPlayerButtonSettings()
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
        await EnsureUnpackagedSettingsLoadedAsync().ConfigureAwait(false);
        return Math.Clamp(GetValue(VolumeKey, SettingsDefaults.Volume), 0.0, 1.0);
    }

    public Task SaveVolumeAsync(double volume)
    {
        return SetValueAsync(VolumeKey, Math.Clamp(volume, 0.0, 1.0));
    }

    public async Task<bool> GetInitialMuteStateAsync()
    {
        await EnsureUnpackagedSettingsLoadedAsync().ConfigureAwait(false);
        return GetValue(MuteStateKey, SettingsDefaults.MuteState);
    }

    public Task SaveMuteStateAsync(bool isMuted)
    {
        return SetValueAsync(MuteStateKey, isMuted);
    }

    public async Task<bool> GetInitialShuffleStateAsync()
    {
        await EnsureUnpackagedSettingsLoadedAsync().ConfigureAwait(false);
        return GetValue(ShuffleStateKey, SettingsDefaults.ShuffleState);
    }

    public Task SaveShuffleStateAsync(bool isEnabled)
    {
        return SetValueAsync(ShuffleStateKey, isEnabled);
    }

    public async Task<RepeatMode> GetInitialRepeatModeAsync()
    {
        await EnsureUnpackagedSettingsLoadedAsync().ConfigureAwait(false);
        return GetEnumValue(RepeatModeKey, SettingsDefaults.DefaultRepeatMode);
    }

    public Task SaveRepeatModeAsync(RepeatMode mode)
    {
        return SetValueAsync(RepeatModeKey, mode.ToString());
    }

    public async Task<bool> GetRestorePlaybackStateEnabledAsync()
    {
        await EnsureUnpackagedSettingsLoadedAsync().ConfigureAwait(false);
        return GetValue(RestorePlaybackStateEnabledKey, SettingsDefaults.RestorePlaybackStateEnabled);
    }

    public Task SetRestorePlaybackStateEnabledAsync(bool isEnabled)
    {
        return SetValueAsync(RestorePlaybackStateEnabledKey, isEnabled);
    }

    public async Task<bool> GetFetchOnlineMetadataEnabledAsync()
    {
        await EnsureUnpackagedSettingsLoadedAsync().ConfigureAwait(false);
        return GetValue(FetchOnlineMetadataKey, SettingsDefaults.FetchOnlineMetadataEnabled);
    }

    public Task SetFetchOnlineMetadataEnabledAsync(bool isEnabled)
    {
        return SetValueAndNotifyAsync(FetchOnlineMetadataKey, isEnabled, SettingsDefaults.FetchOnlineMetadataEnabled, FetchOnlineMetadataEnabledChanged);
    }

    public async Task<bool> GetFetchOnlineLyricsEnabledAsync()
    {
        await EnsureUnpackagedSettingsLoadedAsync().ConfigureAwait(false);
        return GetValue(FetchOnlineLyricsEnabledKey, SettingsDefaults.FetchOnlineLyricsEnabled);
    }

    public Task SetFetchOnlineLyricsEnabledAsync(bool isEnabled)
    {
        return SetValueAndNotifyAsync(FetchOnlineLyricsEnabledKey, isEnabled, SettingsDefaults.FetchOnlineLyricsEnabled, FetchOnlineLyricsEnabledChanged);
    }

    public async Task<bool> GetDiscordRichPresenceEnabledAsync()
    {
        await EnsureUnpackagedSettingsLoadedAsync().ConfigureAwait(false);
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
            await ClearPlaybackStateAsync().ConfigureAwait(false);
            return;
        }

        var jsonState = JsonSerializer.Serialize(state, _serializerOptions);

        if (_isPackaged)
        {
            var stateFile = await ApplicationData.Current.LocalFolder.CreateFileAsync(
                Path.GetFileName(_pathConfig.PlaybackStateFilePath), CreationCollisionOption.ReplaceExisting).AsTask().ConfigureAwait(false);
            await FileIO.WriteTextAsync(stateFile, jsonState).AsTask().ConfigureAwait(false);
        }
        else
        {
            await File.WriteAllTextAsync(_pathConfig.PlaybackStateFilePath, jsonState).ConfigureAwait(false);
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
                    Path.GetFileName(_pathConfig.PlaybackStateFilePath)).AsTask().ConfigureAwait(false);
                if (item is IStorageFile stateFile) jsonState = await FileIO.ReadTextAsync(stateFile).AsTask().ConfigureAwait(false);
            }
            else
            {
                if (!File.Exists(_pathConfig.PlaybackStateFilePath)) return null;
                jsonState = await File.ReadAllTextAsync(_pathConfig.PlaybackStateFilePath).ConfigureAwait(false);
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
            await ClearPlaybackStateAsync().ConfigureAwait(false);
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
                    Path.GetFileName(_pathConfig.PlaybackStateFilePath)).AsTask().ConfigureAwait(false);
                if (item != null) await item.DeleteAsync().AsTask().ConfigureAwait(false);
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
        await EnsureUnpackagedSettingsLoadedAsync().ConfigureAwait(false);
        return GetValue(LastFmScrobblingEnabledKey, SettingsDefaults.LastFmScrobblingEnabled);
    }

    public async Task SetLastFmScrobblingEnabledAsync(bool isEnabled)
    {
        await SetValueAsync(LastFmScrobblingEnabledKey, isEnabled).ConfigureAwait(false);
        LastFmSettingsChanged?.Invoke();
    }

    public async Task<bool> GetLastFmNowPlayingEnabledAsync()
    {
        await EnsureUnpackagedSettingsLoadedAsync().ConfigureAwait(false);
        return GetValue(LastFmNowPlayingEnabledKey, SettingsDefaults.LastFmNowPlayingEnabled);
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

    public async Task<string?> GetLastFmAuthTokenAsync()
    {
        await EnsureUnpackagedSettingsLoadedAsync().ConfigureAwait(false);
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

    public async Task<List<ServiceProviderSetting>> GetServiceProvidersAsync(ServiceCategory category)
    {
        await EnsureUnpackagedSettingsLoadedAsync().ConfigureAwait(false);
        var key = category == ServiceCategory.Lyrics ? LyricsServiceProvidersKey : MetadataServiceProvidersKey;
        var items = await GetComplexValueAsync<List<ServiceProviderSetting>>(key).ConfigureAwait(false);

        if (items is { Count: > 0 })
        {
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

    public async Task<string> GetArtistSplitCharactersAsync()
    {
        await EnsureUnpackagedSettingsLoadedAsync().ConfigureAwait(false);
        return GetValue(ArtistSplitCharactersKey, SettingsDefaults.DefaultArtistSplitCharacters);
    }

    public async Task SetArtistSplitCharactersAsync(string characters)
    {
        await EnsureUnpackagedSettingsLoadedAsync().ConfigureAwait(false);
        var currentValue = GetValue(ArtistSplitCharactersKey, SettingsDefaults.DefaultArtistSplitCharacters);

        await SetValueAsync(ArtistSplitCharactersKey, characters).ConfigureAwait(false);
        if (currentValue != characters) ArtistSplitCharactersChanged?.Invoke();
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
                    Description = "Community-curated lyrics database"
                },
                new()
                {
                    Id = ServiceProviderIds.NetEase,
                    DisplayName = "NetEase",
                    Category = ServiceCategory.Lyrics,
                    IsEnabled = true,
                    Order = 1,
                    Description = "Chinese music service, great for Asian music"
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
                    Description = "Fetches artist IDs (required for TheAudioDB and Fanart.tv)"
                },
                new()
                {
                    Id = ServiceProviderIds.TheAudioDb,
                    DisplayName = "TheAudioDB",
                    Category = ServiceCategory.Metadata,
                    IsEnabled = true,
                    Order = 1,
                    Description = "High-quality artist images and biographies (requires MusicBrainz)"
                },
                new()
                {
                    Id = ServiceProviderIds.FanartTv,
                    DisplayName = "Fanart.tv",
                    Category = ServiceCategory.Metadata,
                    IsEnabled = true,
                    Order = 2,
                    Description = "High-quality artist images only (requires MusicBrainz)"
                },
                new()
                {
                    Id = ServiceProviderIds.Spotify,
                    DisplayName = "Spotify",
                    Category = ServiceCategory.Metadata,
                    IsEnabled = true,
                    Order = 3,
                    Description = "Artist images only"
                },
                new()
                {
                    Id = ServiceProviderIds.LastFm,
                    DisplayName = "Last.fm",
                    Category = ServiceCategory.Metadata,
                    IsEnabled = true,
                    Order = 4,
                    Description = "Artist biographies and images (fallback)"
                }
            },
            _ => new List<ServiceProviderSetting>()
        };
    }

    #endregion

    #region UI Settings (IUISettingsService)

    public async Task<ElementTheme> GetThemeAsync()
    {
        await EnsureUnpackagedSettingsLoadedAsync().ConfigureAwait(false);
        return GetEnumValue(ThemeKey, SettingsDefaults.Theme);
    }

    public Task SetThemeAsync(ElementTheme theme)
    {
        return SetValueAsync(ThemeKey, theme.ToString());
    }

    public async Task<BackdropMaterial> GetBackdropMaterialAsync()
    {
        await EnsureUnpackagedSettingsLoadedAsync().ConfigureAwait(false);
        return GetEnumValue(BackdropMaterialKey, SettingsDefaults.DefaultBackdropMaterial);
    }

    public async Task SetBackdropMaterialAsync(BackdropMaterial material)
    {
        await SetValueAsync(BackdropMaterialKey, material.ToString()).ConfigureAwait(false);
        BackdropMaterialChanged?.Invoke(material);
    }

    public async Task<bool> GetDynamicThemingAsync()
    {
        await EnsureUnpackagedSettingsLoadedAsync().ConfigureAwait(false);
        return GetValue(DynamicThemingKey, SettingsDefaults.DynamicThemingEnabled);
    }

    public Task SetDynamicThemingAsync(bool isEnabled)
    {
        return SetValueAsync(DynamicThemingKey, isEnabled);
    }

    public async Task<bool> GetPlayerAnimationEnabledAsync()
    {
        await EnsureUnpackagedSettingsLoadedAsync().ConfigureAwait(false);
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

        return await Task.Run(() =>
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", false);
            return key?.GetValue(AutoLaunchRegistryValueName) != null;
        }).ConfigureAwait(false);
    }

    public async Task SetAutoLaunchEnabledAsync(bool isEnabled)
    {
        if (_isPackaged)
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
            }).ConfigureAwait(false);
        }
    }

    public async Task<bool> GetStartMinimizedEnabledAsync()
    {
        await EnsureUnpackagedSettingsLoadedAsync().ConfigureAwait(false);
        return GetValue(StartMinimizedEnabledKey, SettingsDefaults.StartMinimizedEnabled);
    }

    public Task SetStartMinimizedEnabledAsync(bool isEnabled)
    {
        return SetValueAsync(StartMinimizedEnabledKey, isEnabled);
    }

    public async Task<bool> GetHideToTrayEnabledAsync()
    {
        await EnsureUnpackagedSettingsLoadedAsync().ConfigureAwait(false);
        return GetValue(HideToTrayEnabledKey, SettingsDefaults.HideToTrayEnabled);
    }

    public Task SetHideToTrayEnabledAsync(bool isEnabled)
    {
        return SetValueAndNotifyAsync(HideToTrayEnabledKey, isEnabled, SettingsDefaults.HideToTrayEnabled, HideToTraySettingChanged);
    }

    public async Task<bool> GetMinimizeToMiniPlayerEnabledAsync()
    {
        await EnsureUnpackagedSettingsLoadedAsync().ConfigureAwait(false);
        return GetValue(MinimizeToMiniPlayerEnabledKey, SettingsDefaults.MinimizeToMiniPlayerEnabled);
    }

    public Task SetMinimizeToMiniPlayerEnabledAsync(bool isEnabled)
    {
        return SetValueAndNotifyAsync(MinimizeToMiniPlayerEnabledKey, isEnabled, SettingsDefaults.MinimizeToMiniPlayerEnabled,
            MinimizeToMiniPlayerSettingChanged);
    }

    public async Task<bool> GetShowQueueButtonEnabledAsync()
    {
        await EnsureUnpackagedSettingsLoadedAsync().ConfigureAwait(false);
        return GetValue(ShowQueueButtonEnabledKey, SettingsDefaults.ShowQueueButtonEnabled);
    }

    public Task SetShowQueueButtonEnabledAsync(bool isEnabled)
    {
        return SetValueAndNotifyAsync(ShowQueueButtonEnabledKey, isEnabled, SettingsDefaults.ShowQueueButtonEnabled, ShowQueueButtonSettingChanged);
    }

    public async Task<bool> GetShowCoverArtInTrayFlyoutAsync()
    {
        await EnsureUnpackagedSettingsLoadedAsync().ConfigureAwait(false);
        return GetValue(ShowCoverArtInTrayFlyoutKey, SettingsDefaults.ShowCoverArtInTrayFlyoutEnabled);
    }

    public Task SetShowCoverArtInTrayFlyoutAsync(bool isEnabled)
    {
        return SetValueAndNotifyAsync(ShowCoverArtInTrayFlyoutKey, isEnabled, SettingsDefaults.ShowCoverArtInTrayFlyoutEnabled,
            ShowCoverArtInTrayFlyoutSettingChanged);
    }

    public async Task<Windows.UI.Color?> GetAccentColorAsync()
    {
        await EnsureUnpackagedSettingsLoadedAsync().ConfigureAwait(false);
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
        await EnsureUnpackagedSettingsLoadedAsync().ConfigureAwait(false);
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
        await EnsureUnpackagedSettingsLoadedAsync().ConfigureAwait(false);
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
        var items = await GetComplexValueAsync<List<NavigationItemSetting>>(NavigationItemsKey).ConfigureAwait(false);

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
        await SetValueAsync(NavigationItemsKey, items).ConfigureAwait(false);
        NavigationSettingsChanged?.Invoke();
    }

    public async Task<List<PlayerButtonSetting>> GetPlayerButtonSettingsAsync()
    {
        var settings = await GetComplexValueAsync<List<PlayerButtonSetting>>(PlayerButtonSettingsKey).ConfigureAwait(false);

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
        await SetValueAsync(PlayerButtonSettingsKey, settings).ConfigureAwait(false);
        PlayerButtonSettingsChanged?.Invoke();
    }

    public async Task<bool> GetRememberWindowSizeEnabledAsync()
    {
        await EnsureUnpackagedSettingsLoadedAsync().ConfigureAwait(false);
        return GetValue(RememberWindowSizeEnabledKey, SettingsDefaults.RememberWindowSizeEnabled);
    }

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

    public async Task<bool> GetRememberWindowPositionEnabledAsync()
    {
        await EnsureUnpackagedSettingsLoadedAsync().ConfigureAwait(false);
        return GetValue(RememberWindowPositionEnabledKey, SettingsDefaults.RememberWindowPositionEnabled);
    }

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

    public async Task<bool> GetRememberPaneStateEnabledAsync()
    {
        await EnsureUnpackagedSettingsLoadedAsync().ConfigureAwait(false);
        return GetValue(RememberPaneStateEnabledKey, SettingsDefaults.RememberPaneStateEnabled);
    }

    public Task SetRememberPaneStateEnabledAsync(bool isEnabled)
    {
        return SetValueAsync(RememberPaneStateEnabledKey, isEnabled);
    }

    public async Task<bool?> GetLastPaneOpenAsync()
    {
        await EnsureUnpackagedSettingsLoadedAsync().ConfigureAwait(false);
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
        await EnsureUnpackagedSettingsLoadedAsync().ConfigureAwait(false);
        return GetValue(VolumeNormalizationEnabledKey, SettingsDefaults.VolumeNormalizationEnabled);
    }

    public Task SetVolumeNormalizationEnabledAsync(bool isEnabled)
    {
        return SetValueAndNotifyAsync(VolumeNormalizationEnabledKey, isEnabled, SettingsDefaults.VolumeNormalizationEnabled,
            VolumeNormalizationEnabledChanged);
    }

    public async Task<TEnum> GetSortOrderAsync<TEnum>(string pageKey) where TEnum : struct, Enum
    {
        await EnsureUnpackagedSettingsLoadedAsync().ConfigureAwait(false);
        var defaultValue = GetDefaultSortOrder<TEnum>(pageKey);
        return GetEnumValue(pageKey, defaultValue);
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

    public async Task FlushAsync()
    {
        if (_disposed) return;
        if (_isPackaged)
        {
            _logger.LogDebug("FlushAsync skipped: packaged app uses ApplicationData which handles persistence automatically.");
            return;
        }

        try
        {
            await _settingsFileLock.WaitAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // Semaphore was disposed (service is shutting down), nothing we can do.
            _logger.LogDebug("FlushAsync aborted: semaphore was disposed.");
            return;
        }
        
        try
        {
            string json;
            lock (_dictLock)
            {
                json = JsonSerializer.Serialize(_settings, _serializerOptions);
            }
            await File.WriteAllTextAsync(_pathConfig.SettingsFilePath, json).ConfigureAwait(false);
            
            // Critical: Reset the queued flag so that the debounced QueueSaveAsync
            // doesn't think it still needs to run if it wakes up later (though it won't matter much).
            Interlocked.Exchange(ref _isSaveQueued, 0);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to flush settings to file.");
        }
        finally
        {
            _settingsFileLock.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _uiSettings.AdvancedEffectsEnabledChanged -= OnAdvancedEffectsEnabledChanged;
        _settingsFileLock.Dispose();
        GC.SuppressFinalize(this);
    }

    #endregion
}
