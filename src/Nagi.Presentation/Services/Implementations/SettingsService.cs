using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Nagi.Core.Helpers;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Presentation.Models;
using Nagi.Presentation.Navigation;
using Nagi.Presentation.Resources;
using Nagi.Presentation.Services.Abstractions;

namespace Nagi.Presentation.Services.Implementations;

/// <summary>
///     Reads and writes application settings in the platform's <see cref="ISettingsStore" />.
/// </summary>
public partial class SettingsService : IUISettingsService
{
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
    private const string ShowCoverArtInTrayFlyoutKey = "ShowCoverArtInTrayFlyout";
    private const string FetchOnlineMetadataKey = "FetchOnlineMetadataEnabled";
    private const string FetchOnlineLyricsEnabledKey = "FetchOnlineLyricsEnabled";
    private const string LyricsRomanizationEnabledKey = "LyricsRomanizationEnabled";
    private const string DiscordRichPresenceEnabledKey = "DiscordRichPresenceEnabled";
    private const string NavigationItemsKey = "NavigationItems";
    private const string PlayerButtonSettingsKey = "PlayerButtonSettings";
    private const string QueueDisplaySettingsKey = "QueueDisplaySettings";
    private const string SongListColumnsKey = "SongListColumns";
    private const string ShowSongListHeaderKey = "ShowSongListHeader";
    private const string LastFmCredentialResource = "Nagi/LastFm";
    private const string LastFmAuthTokenKey = "LastFmAuthToken";
    private const string LastFmScrobblingEnabledKey = "LastFmScrobblingEnabled";
    private const string LastFmNowPlayingEnabledKey = "LastFmNowPlayingEnabled";
    private const string ListenBrainzScrobblingEnabledKey = "ListenBrainzScrobblingEnabled";
    private const string ListenBrainzNowPlayingEnabledKey = "ListenBrainzNowPlayingEnabled";
    private const string ListenBrainzServerUrlKey = "ListenBrainzServerUrl";
    private const string ListenBrainzEnabledSinceUtcKey = "ListenBrainzEnabledSinceUtcTicks";
    private const string ListenBrainzTokenResource = "Nagi/ListenBrainz";
    private const string ListenBrainzTokenUserName = "token";
    private const string EqualizerSettingsKey = "EqualizerSettings";
    private const string RememberWindowSizeEnabledKey = "RememberWindowSizeEnabled";
    private const string LastWindowSizeKey = "LastWindowSize";
    private const string RememberWindowPositionEnabledKey = "RememberWindowPositionEnabled";
    private const string LastWindowPositionKey = "LastWindowPosition";
    private const string RememberPaneStateEnabledKey = "RememberPaneStateEnabled";
    private const string LastPaneOpenKey = "LastPaneOpen";
    private const string VolumeNormalizationEnabledKey = "VolumeNormalizationEnabled";
    private const string WasapiExclusiveModeEnabledKey = "WasapiExclusiveModeEnabled";
    private const string AudioOutputDeviceIdKey = "AudioOutputDeviceId";
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
    private const string IgnoreLeadingArticlesOnSortKey = "IgnoreLeadingArticlesOnSort";

    private readonly IAutoLaunchService _autoLaunchService;
    private readonly ICredentialLockerService _credentialLockerService;
    private readonly IDictionary<string, object> _values;
    private readonly ILogger<SettingsService> _logger;
    private readonly IPathConfiguration _pathConfig;

    public SettingsService(ISettingsStore settingsStore, IPathConfiguration pathConfig,
        ICredentialLockerService credentialLockerService, IAutoLaunchService autoLaunchService, ILogger<SettingsService> logger)
    {
        _pathConfig = pathConfig;
        _credentialLockerService =
            credentialLockerService;
        _autoLaunchService = autoLaunchService;
        _logger = logger;

        _values = (settingsStore).Values;
    }

    public event Action<bool>? PlayerAnimationSettingChanged;
    public event Action<bool>? HideToTraySettingChanged;
    public event Action<bool>? MinimizeToMiniPlayerSettingChanged;
    public event Action<bool>? ShowCoverArtInTrayFlyoutSettingChanged;
    public event Action? NavigationSettingsChanged;
    public event Action? PlayerButtonSettingsChanged;
    public event Action<QueueDisplaySettings>? QueueDisplaySettingsChanged;
    public event Action<SongListColumns>? SongListColumnsChanged;
    public event Action<bool>? ShowSongListHeaderSettingChanged;
    public event Action? LastFmSettingsChanged;
    public event Action? ListenBrainzSettingsChanged;
    public event Action<bool>? DiscordRichPresenceSettingChanged;
    public event Action<bool>? VolumeNormalizationEnabledChanged;
    public event Action<bool>? FadeOnPlayPauseEnabledChanged;
    public event Action<int>? FadeInDurationChanged;
    public event Action<int>? FadeOutDurationChanged;
    public event Action<BackdropMaterial>? BackdropMaterialChanged;
    public event Action<bool>? FetchOnlineMetadataEnabledChanged;
    public event Action<bool>? FetchOnlineLyricsEnabledChanged;
    public event Action<bool>? LyricsRomanizationEnabledChanged;
    public event Action? PlayerDesignSettingsChanged;
    public event Action<int>? SongsPerPageChanged;
    public event Action<bool>? IgnoreLeadingArticlesOnSortEnabledChanged;

    public async Task ResetToDefaultsAsync()
    {
        await ResetStoredValuesAsync().ConfigureAwait(false);
        await Task.WhenAll(ClearPlaybackStateAsync(),
            _autoLaunchService.SetEnabledAsync(SettingsDefaults.AutoLaunchEnabled),
            ClearLastFmCredentialsAsync(), ClearListenBrainzUserTokenAsync()).ConfigureAwait(false);
        _logger.LogInformation("All application settings have been reset to their default values.");
    }

    public Task<string> ExportAsync()
    {
        // The Last.fm auth token is only valid during one sign-in.
        var snapshot = _values.Where(entry => entry.Key != LastFmAuthTokenKey).ToDictionary(
            entry => entry.Key,
            entry => new StoredSetting(entry.Value.GetType().Name, JsonSerializer.SerializeToElement(entry.Value)));
        return Task.FromResult(JsonSerializer.Serialize(snapshot));
    }

    public Task ImportAsync(string snapshot)
    {
        Dictionary<string, object> values;
        try
        {
            var entries = JsonSerializer.Deserialize<Dictionary<string, StoredSetting>>(snapshot)
                          ?? throw new JsonException("The settings snapshot is empty.");
            values = entries.ToDictionary(entry => entry.Key, entry => ToStoredValue(entry.Value));
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            throw new JsonException("The settings snapshot has a value of the wrong type.", ex);
        }

        _values.Clear();
        foreach (var (key, value) in values) _values[key] = value;
        _logger.LogInformation("Imported {Count} settings.", values.Count);
        return Task.CompletedTask;
    }

    // Getters check the stored type, so the snapshot records it rather than relying on JSON number kinds.
    private static object ToStoredValue(StoredSetting setting) => setting.Type switch
    {
        nameof(Boolean) => setting.Value.GetBoolean(),
        nameof(Int32) => setting.Value.GetInt32(),
        nameof(Int64) => setting.Value.GetInt64(),
        nameof(Double) => setting.Value.GetDouble(),
        nameof(String) => setting.Value.GetString()!,
        _ => throw new JsonException($"Unsupported setting type '{setting.Type}'.")
    };

    private sealed record StoredSetting(string Type, JsonElement Value);

    internal async Task ResetStoredValuesAsync()
    {
        var tasks = new List<Task>
        {
            SetPlayerAnimationEnabledAsync(SettingsDefaults.PlayerAnimationEnabled),
            SetHideToTrayEnabledAsync(SettingsDefaults.HideToTrayEnabled),
            SetMinimizeToMiniPlayerEnabledAsync(SettingsDefaults.MinimizeToMiniPlayerEnabled),
            SetShowCoverArtInTrayFlyoutAsync(SettingsDefaults.ShowCoverArtInTrayFlyoutEnabled),
            SetFetchOnlineMetadataEnabledAsync(SettingsDefaults.FetchOnlineMetadataEnabled),
            SetFetchOnlineLyricsEnabledAsync(SettingsDefaults.FetchOnlineLyricsEnabled),
            SetLyricsRomanizationEnabledAsync(SettingsDefaults.LyricsRomanizationEnabled),
            SetDiscordRichPresenceEnabledAsync(SettingsDefaults.DiscordRichPresenceEnabled),
            SetThemeAsync(SettingsDefaults.Theme),
            SetBackdropMaterialAsync(SettingsDefaults.DefaultBackdropMaterial),
            SetDynamicThemingAsync(SettingsDefaults.DynamicThemingEnabled),
            SetRestorePlaybackStateEnabledAsync(SettingsDefaults.RestorePlaybackStateEnabled),
            SetStartMinimizedEnabledAsync(SettingsDefaults.StartMinimizedEnabled),
            SetNavigationItemsAsync(GetDefaultNavigationItems()),
            SetPlayerButtonSettingsAsync(GetDefaultPlayerButtonSettings()),
            SetQueueDisplaySettingsAsync(new QueueDisplaySettings()),
            SetSongListColumnsAsync(SettingsDefaults.DefaultSongListColumns),
            SetShowSongListHeaderAsync(SettingsDefaults.ShowSongListHeaderEnabled),
            SaveVolumeAsync(SettingsDefaults.Volume),
            SaveMuteStateAsync(SettingsDefaults.MuteState),
            SaveShuffleStateAsync(SettingsDefaults.ShuffleState),
            SaveRepeatModeAsync(SettingsDefaults.DefaultRepeatMode),
            SetLastFmScrobblingEnabledAsync(SettingsDefaults.LastFmScrobblingEnabled),
            SetLastFmNowPlayingEnabledAsync(SettingsDefaults.LastFmNowPlayingEnabled),
            SetListenBrainzScrobblingEnabledAsync(false),
            SetListenBrainzNowPlayingEnabledAsync(false),
            SetListenBrainzServerUrlAsync(null),
            SetListenBrainzEnabledSinceUtcAsync(null),
            SetEqualizerSettingsAsync(new EqualizerSettings()),
            SetRememberWindowSizeEnabledAsync(SettingsDefaults.RememberWindowSizeEnabled),
            SetRememberWindowPositionEnabledAsync(SettingsDefaults.RememberWindowPositionEnabled),
            SetRememberPaneStateEnabledAsync(SettingsDefaults.RememberPaneStateEnabled),
            SetVolumeNormalizationEnabledAsync(SettingsDefaults.VolumeNormalizationEnabled),
            SetWasapiExclusiveModeEnabledAsync(SettingsDefaults.WasapiExclusiveModeEnabled),
            SetAudioOutputDeviceIdAsync(string.Empty),
            SetFadeOnPlayPauseEnabledAsync(SettingsDefaults.FadeOnPlayPauseEnabled),
            SetFadeInDurationMsAsync(SettingsDefaults.DefaultFadeInDurationMs),
            SetFadeOutDurationMsAsync(SettingsDefaults.DefaultFadeOutDurationMs),
            SetAccentColorAsync(SettingsDefaults.AccentColor),
            SetArtistSplitCharactersAsync(SettingsDefaults.DefaultArtistSplitCharacters),
            SetLanguageAsync(string.Empty),
            SetPlayerBackgroundMaterialAsync(SettingsDefaults.DefaultPlayerBackgroundMaterial),
            SetPlayerTintIntensityAsync(SettingsDefaults.DefaultPlayerTintIntensity),
            SetSongsPerPageAsync(SettingsDefaults.DefaultSongsPerPage),
            SetGenreSplitCharactersAsync(SettingsDefaults.DefaultGenreSplitCharacters),
            SetIgnoreLeadingArticlesOnSortEnabledAsync(SettingsDefaults.IgnoreLeadingArticlesOnSortEnabled)
        };

        await Task.WhenAll(tasks).ConfigureAwait(false);
        // Setters must see the old values so live services receive reset notifications.
        _values.Clear();
    }

    private T GetValue<T>(string key, T defaultValue)
    {
        return _values.TryGetValue(key, out var value) && value is T v ? v : defaultValue;
    }

    private T? GetComplexValue<T>(string key) where T : class
    {
        string? json = null;

        if (_values.TryGetValue(key, out var value) && value is string jsonString) json = jsonString;

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

        if (_values.TryGetValue(key, out var value) && value is string stringValue)
            name = stringValue;

        if (name != null && Enum.TryParse(name, out TEnum result) && Enum.IsDefined(result)) return result;

        return defaultValue;
    }

    private Task SetValueAsync<T>(string key, T value)
    {
        if (value is null)
        {
            _values.Remove(key);
            return Task.CompletedTask;
        }

        if (typeof(T).IsClass && typeof(T) != typeof(string))
            _values[key] = JsonSerializer.Serialize(value);
        else
            _values[key] = value!;

        return Task.CompletedTask;
    }

    private async Task SetValueAndNotifyAsync<T>(string key, T newValue, T defaultValue, Action<T>? notifier)
    {
        var currentValue = GetValue(key, defaultValue);

        await SetValueAsync(key, newValue).ConfigureAwait(false);
        if (!EqualityComparer<T>.Default.Equals(currentValue, newValue)) notifier?.Invoke(newValue);
    }

    private static List<NavigationItemSetting> GetDefaultNavigationItems()
    {
        return new List<NavigationItemSetting>
        {
            new() { DisplayName = Strings.Settings_Nav_Library, Tag = "Library", Icon = AppIcon.Library, IsEnabled = true },
            new() { DisplayName = Strings.Settings_Nav_Folders, Tag = "Folders", Icon = AppIcon.Folders, IsEnabled = true },
            new() { DisplayName = Strings.Settings_Nav_Playlists, Tag = "Playlists", Icon = AppIcon.Playlists, IsEnabled = true },
            new() { DisplayName = Strings.Settings_Nav_Artists, Tag = "Artists", Icon = AppIcon.Artists, IsEnabled = true },
            new() { DisplayName = Strings.Settings_Nav_Albums, Tag = "Albums", Icon = AppIcon.Albums, IsEnabled = true },
            new() { DisplayName = Strings.Settings_Nav_Genres, Tag = "Genres", Icon = AppIcon.Genres, IsEnabled = true },
            new() { DisplayName = Strings.Settings_Nav_Insights, Tag = "Insights", Icon = AppIcon.Insights, IsEnabled = false },
            new() { DisplayName = Strings.Listening_Favorites, Tag = "Favorites", Icon = AppIcon.Favorites, IsEnabled = false }
        };
    }

    public List<PlayerButtonSetting> GetDefaultPlayerButtonSettings()
    {
        return new List<PlayerButtonSetting>
        {
            new() { Id = "Shuffle", DisplayName = Strings.Settings_Button_Shuffle, Icon = AppIcon.Shuffle, IsEnabled = true },
            new() { Id = "Previous", DisplayName = Strings.Settings_Button_Previous, Icon = AppIcon.Previous, IsEnabled = true },
            new() { Id = "PlayPause", DisplayName = Strings.Settings_Button_PlayPause, Icon = AppIcon.Play, IsEnabled = true },
            new() { Id = "Next", DisplayName = Strings.Settings_Button_Next, Icon = AppIcon.Next, IsEnabled = true },
            new() { Id = "Repeat", DisplayName = Strings.Settings_Button_Repeat, Icon = AppIcon.RepeatAll, IsEnabled = true },
            new() { Id = "Separator", DisplayName = Strings.Settings_Button_Divider, IsEnabled = true },
            new() { Id = "Lyrics", DisplayName = Strings.Settings_Button_Lyrics, Icon = AppIcon.Lyrics, IsEnabled = true },
            new() { Id = "Queue", DisplayName = Strings.Settings_Button_Queue, Icon = AppIcon.Queue, IsEnabled = true },
            new() { Id = "Volume", DisplayName = Strings.Settings_Button_Volume, Icon = AppIcon.VolumeHigh, IsEnabled = true }
        };
    }
}
