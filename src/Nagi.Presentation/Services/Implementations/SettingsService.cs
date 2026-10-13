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
        _pathConfig = pathConfig ?? throw new ArgumentNullException(nameof(pathConfig));
        _credentialLockerService =
            credentialLockerService ?? throw new ArgumentNullException(nameof(credentialLockerService));
        _autoLaunchService = autoLaunchService ?? throw new ArgumentNullException(nameof(autoLaunchService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _values = (settingsStore ?? throw new ArgumentNullException(nameof(settingsStore))).Values;
    }

    public event Action<bool>? PlayerAnimationSettingChanged;
    public event Action<bool>? HideToTraySettingChanged;
    public event Action<bool>? MinimizeToMiniPlayerSettingChanged;
    public event Action<bool>? ShowCoverArtInTrayFlyoutSettingChanged;
    public event Action? NavigationSettingsChanged;
    public event Action? PlayerButtonSettingsChanged;
    public event Action<QueueDisplaySettings>? QueueDisplaySettingsChanged;
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
            new() { DisplayName = Resources.Strings.Settings_Nav_Library, Tag = "Library", IconGlyph = "\uE8F1", IsEnabled = true },
            new() { DisplayName = Resources.Strings.Settings_Nav_Folders, Tag = "Folders", IconGlyph = "\uE8B7", IsEnabled = true },
            new() { DisplayName = Resources.Strings.Settings_Nav_Playlists, Tag = "Playlists", IconGlyph = "\uE90B", IsEnabled = true },
            new() { DisplayName = Resources.Strings.Settings_Nav_Artists, Tag = "Artists", IconGlyph = "\uE77B", IsEnabled = true },
            new() { DisplayName = Resources.Strings.Settings_Nav_Albums, Tag = "Albums", IconGlyph = "\uE93C", IsEnabled = true },
            new() { DisplayName = Resources.Strings.Settings_Nav_Genres, Tag = "Genres", IconGlyph = "\uE8EC", IsEnabled = true },
            new() { DisplayName = Resources.Strings.Settings_Nav_Insights, Tag = "Insights", IconGlyph = "\uE9D2", IsEnabled = false },
            new() { DisplayName = Resources.Strings.Listening_Favorites, Tag = "Favorites", IconGlyph = "\uEB51", IsEnabled = false }
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
            new() { Id = "Separator", DisplayName = Resources.Strings.Settings_Button_Divider, IconGlyph = string.Empty, IsEnabled = true },
            new() { Id = "Lyrics", DisplayName = Resources.Strings.Settings_Button_Lyrics, IconGlyph = "\uED1E", IsEnabled = true },
            new() { Id = "Queue", DisplayName = Resources.Strings.Settings_Button_Queue, IconGlyph = "\uE90B", IsEnabled = true },
            new() { Id = "Volume", DisplayName = Resources.Strings.Settings_Button_Volume, IconGlyph = "\uE767", IsEnabled = true }
        };
    }
}
