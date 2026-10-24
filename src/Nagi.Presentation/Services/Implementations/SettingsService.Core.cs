using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Nagi.Core.Models;
using Nagi.Core.Services.Data;
using Nagi.Presentation.Models;
using Nagi.Presentation.Resources;

namespace Nagi.Presentation.Services.Implementations;

public partial class SettingsService
{
    public Task<double> GetInitialVolumeAsync()
    {
        return Task.FromResult(Math.Clamp(GetValue(VolumeKey, SettingsDefaults.Volume), 0.0, 1.0));
    }

    public Task SaveVolumeAsync(double volume)
    {
        return SetValueAsync(VolumeKey, Math.Clamp(volume, 0.0, 1.0));
    }

    public Task<bool> GetInitialMuteStateAsync()
    {
        return Task.FromResult(GetValue(MuteStateKey, SettingsDefaults.MuteState));
    }

    public Task SaveMuteStateAsync(bool isMuted)
    {
        return SetValueAsync(MuteStateKey, isMuted);
    }

    public Task<bool> GetInitialShuffleStateAsync()
    {
        return Task.FromResult(GetValue(ShuffleStateKey, SettingsDefaults.ShuffleState));
    }

    public Task SaveShuffleStateAsync(bool isEnabled)
    {
        return SetValueAsync(ShuffleStateKey, isEnabled);
    }

    public Task<RepeatMode> GetInitialRepeatModeAsync()
    {
        return Task.FromResult(GetEnumValue(RepeatModeKey, SettingsDefaults.DefaultRepeatMode));
    }

    public Task SaveRepeatModeAsync(RepeatMode mode)
    {
        return SetValueAsync(RepeatModeKey, mode.ToString());
    }

    public Task<bool> GetRestorePlaybackStateEnabledAsync()
    {
        return Task.FromResult(GetValue(RestorePlaybackStateEnabledKey, SettingsDefaults.RestorePlaybackStateEnabled));
    }

    public Task SetRestorePlaybackStateEnabledAsync(bool isEnabled)
    {
        return SetValueAsync(RestorePlaybackStateEnabledKey, isEnabled);
    }

    public Task<bool> GetFetchOnlineMetadataEnabledAsync()
    {
        return Task.FromResult(GetValue(FetchOnlineMetadataKey, SettingsDefaults.FetchOnlineMetadataEnabled));
    }

    public Task SetFetchOnlineMetadataEnabledAsync(bool isEnabled)
    {
        return SetValueAndNotifyAsync(FetchOnlineMetadataKey, isEnabled, SettingsDefaults.FetchOnlineMetadataEnabled, FetchOnlineMetadataEnabledChanged);
    }

    public Task<bool> GetFetchOnlineLyricsEnabledAsync()
    {
        return Task.FromResult(GetValue(FetchOnlineLyricsEnabledKey, SettingsDefaults.FetchOnlineLyricsEnabled));
    }

    public Task SetFetchOnlineLyricsEnabledAsync(bool isEnabled)
    {
        return SetValueAndNotifyAsync(FetchOnlineLyricsEnabledKey, isEnabled, SettingsDefaults.FetchOnlineLyricsEnabled, FetchOnlineLyricsEnabledChanged);
    }

    public Task<bool> GetLyricsRomanizationEnabledAsync()
    {
        return Task.FromResult(GetValue(LyricsRomanizationEnabledKey, SettingsDefaults.LyricsRomanizationEnabled));
    }

    public Task SetLyricsRomanizationEnabledAsync(bool isEnabled)
    {
        return SetValueAndNotifyAsync(LyricsRomanizationEnabledKey, isEnabled, SettingsDefaults.LyricsRomanizationEnabled, LyricsRomanizationEnabledChanged);
    }

    public Task<bool> GetDiscordRichPresenceEnabledAsync()
    {
        return Task.FromResult(GetValue(DiscordRichPresenceEnabledKey, SettingsDefaults.DiscordRichPresenceEnabled));
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

        var path = _pathConfig.PlaybackStateFilePath;
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(temporaryPath, JsonSerializer.Serialize(state)).ConfigureAwait(false);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporaryPath); }
            catch (Exception ex) { _logger.LogWarning(ex, "Could not remove temporary playback state {Path}.", temporaryPath); }
        }
    }

    public async Task<PlaybackState?> GetPlaybackStateAsync()
    {
        try
        {
            var json = await File.ReadAllTextAsync(_pathConfig.PlaybackStateFilePath).ConfigureAwait(false);
            return JsonSerializer.Deserialize<PlaybackState>(json);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Discarding invalid playback state.");
            await ClearPlaybackStateAsync().ConfigureAwait(false);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error reading playback state.");
            return null;
        }
    }

    public Task ClearPlaybackStateAsync()
    {
        try
        {
            File.Delete(_pathConfig.PlaybackStateFilePath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error clearing playback state file.");
        }
        return Task.CompletedTask;
    }

    public Task<bool> GetLastFmScrobblingEnabledAsync()
    {
        return Task.FromResult(GetValue(LastFmScrobblingEnabledKey, SettingsDefaults.LastFmScrobblingEnabled));
    }

    public async Task SetLastFmScrobblingEnabledAsync(bool isEnabled)
    {
        await SetValueAsync(LastFmScrobblingEnabledKey, isEnabled).ConfigureAwait(false);
        LastFmSettingsChanged?.Invoke();
    }

    public Task<bool> GetLastFmNowPlayingEnabledAsync()
    {
        return Task.FromResult(GetValue(LastFmNowPlayingEnabledKey, SettingsDefaults.LastFmNowPlayingEnabled));
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

    public Task<string?> GetLastFmAuthTokenAsync()
    {
        return Task.FromResult(GetValue<string?>(LastFmAuthTokenKey, null));
    }

    public Task<bool> GetListenBrainzScrobblingEnabledAsync()
    {
        return Task.FromResult(GetValue(ListenBrainzScrobblingEnabledKey, false));
    }

    public async Task SetListenBrainzScrobblingEnabledAsync(bool isEnabled)
    {
        await SetValueAsync(ListenBrainzScrobblingEnabledKey, isEnabled).ConfigureAwait(false);
        ListenBrainzSettingsChanged?.Invoke();
    }

    public Task<bool> GetListenBrainzNowPlayingEnabledAsync()
    {
        return Task.FromResult(GetValue(ListenBrainzNowPlayingEnabledKey, false));
    }

    public async Task SetListenBrainzNowPlayingEnabledAsync(bool isEnabled)
    {
        await SetValueAsync(ListenBrainzNowPlayingEnabledKey, isEnabled).ConfigureAwait(false);
        ListenBrainzSettingsChanged?.Invoke();
    }

    public Task<string?> GetListenBrainzUserTokenAsync()
    {
        var credential = _credentialLockerService.RetrieveCredential(ListenBrainzTokenResource);
        return Task.FromResult(credential?.Password);
    }

    public Task SaveListenBrainzUserTokenAsync(string token)
    {
        _credentialLockerService.SaveCredential(ListenBrainzTokenResource, ListenBrainzTokenUserName, token);
        ListenBrainzSettingsChanged?.Invoke();
        return Task.CompletedTask;
    }

    public Task ClearListenBrainzUserTokenAsync()
    {
        _credentialLockerService.RemoveCredential(ListenBrainzTokenResource);
        ListenBrainzSettingsChanged?.Invoke();
        return Task.CompletedTask;
    }

    public Task<string?> GetListenBrainzServerUrlAsync()
    {
        return Task.FromResult(GetValue<string?>(ListenBrainzServerUrlKey, null));
    }

    public async Task SetListenBrainzServerUrlAsync(string? url)
    {
        await SetValueAsync<string?>(ListenBrainzServerUrlKey, url).ConfigureAwait(false);
        ListenBrainzSettingsChanged?.Invoke();
    }

    public Task<DateTime?> GetListenBrainzEnabledSinceUtcAsync()
    {
        var ticks = GetValue<long>(ListenBrainzEnabledSinceUtcKey, 0L);
        if (ticks == 0L) return Task.FromResult<DateTime?>(null);
        return Task.FromResult<DateTime?>(new DateTime(ticks, DateTimeKind.Utc));
    }

    public async Task SetListenBrainzEnabledSinceUtcAsync(DateTime? timestamp)
    {
        var ticks = timestamp.HasValue ? timestamp.Value.ToUniversalTime().Ticks : 0L;
        await SetValueAsync(ListenBrainzEnabledSinceUtcKey, ticks).ConfigureAwait(false);
        ListenBrainzSettingsChanged?.Invoke();
    }

    public Task<EqualizerSettings?> GetEqualizerSettingsAsync()
    {
        return Task.FromResult(GetComplexValue<EqualizerSettings>(EqualizerSettingsKey));
    }

    public Task SetEqualizerSettingsAsync(EqualizerSettings settings)
    {
        return SetValueAsync(EqualizerSettingsKey, settings);
    }

    public Task<List<ServiceProviderSetting>> GetServiceProvidersAsync(ServiceCategory category)
    {
        var key = category == ServiceCategory.Lyrics ? LyricsServiceProvidersKey : MetadataServiceProvidersKey;
        var saved = GetComplexValue<List<ServiceProviderSetting>>(key) ?? [];
        var defaults = GetDefaultServiceProviders(category);
        var providers = saved.DistinctBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p.Order)
            .Join(defaults, p => p.Id, p => p.Id, (configuration, definition) =>
            {
                definition.IsEnabled = configuration.IsEnabled;
                return definition;
            }, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // A source added by an update goes below every source ranked above it by default, wherever the user moved
        // those, so it never outranks a source the user already preferred.
        for (var i = 0; i < defaults.Count; i++)
        {
            if (providers.Any(p => string.Equals(p.Id, defaults[i].Id, StringComparison.OrdinalIgnoreCase))) continue;
            var higherRanked = defaults.Take(i).Select(d => d.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var insertAt = providers.FindLastIndex(p => higherRanked.Contains(p.Id)) + 1;
            providers.Insert(insertAt, defaults[i]);
        }

        for (var i = 0; i < providers.Count; i++) providers[i].Order = i;
        return Task.FromResult(providers);
    }

    public async Task SetServiceProvidersAsync(ServiceCategory category, List<ServiceProviderSetting> providers)
    {
        // Normalize order values based on list position
        for (var i = 0; i < providers.Count; i++)
            providers[i].Order = i;

        var key = category == ServiceCategory.Lyrics ? LyricsServiceProvidersKey : MetadataServiceProvidersKey;
        await SetValueAsync(key, providers).ConfigureAwait(false);
    }

    public async Task<List<ServiceProviderSetting>> GetEnabledServiceProvidersAsync(ServiceCategory category)
    {
        var providers = await GetServiceProvidersAsync(category).ConfigureAwait(false);
        return providers.Where(p => p.IsEnabled).ToList();
    }

    public Task<string> GetArtistSplitCharactersAsync()
    {
        return Task.FromResult(GetValue(ArtistSplitCharactersKey, SettingsDefaults.DefaultArtistSplitCharacters));
    }

    public Task SetArtistSplitCharactersAsync(string characters) => SetValueAsync(ArtistSplitCharactersKey, characters);

    public Task<bool> GetIgnoreLeadingArticlesOnSortEnabledAsync()
        => Task.FromResult(GetValue(IgnoreLeadingArticlesOnSortKey, SettingsDefaults.IgnoreLeadingArticlesOnSortEnabled));

    public Task SetIgnoreLeadingArticlesOnSortEnabledAsync(bool isEnabled)
        => SetValueAndNotifyAsync(IgnoreLeadingArticlesOnSortKey, isEnabled, SettingsDefaults.IgnoreLeadingArticlesOnSortEnabled, IgnoreLeadingArticlesOnSortEnabledChanged);

    public Task<string> GetGenreSplitCharactersAsync() => Task.FromResult(GetValue(GenreSplitCharactersKey, SettingsDefaults.DefaultGenreSplitCharacters));

    public Task SetGenreSplitCharactersAsync(string characters) => SetValueAsync(GenreSplitCharactersKey, characters);

    public Task<string> GetLanguageAsync() => Task.FromResult(GetValue(LanguageKey, string.Empty));

    public Task SetLanguageAsync(string languageCode) =>
        SetValueAsync(LanguageKey, languageCode);

    public Task<PlayerBackgroundMaterial> GetPlayerBackgroundMaterialAsync() => Task.FromResult(GetEnumValue(PlayerBackgroundMaterialKey, PlayerBackgroundMaterial.Acrylic));

    public Task SetPlayerBackgroundMaterialAsync(PlayerBackgroundMaterial material)
    {
        return SetValueAndNotifyAsync(PlayerBackgroundMaterialKey, material.ToString(), PlayerBackgroundMaterial.Acrylic.ToString(), _ => PlayerDesignSettingsChanged?.Invoke());
    }

    public Task<double> GetPlayerTintIntensityAsync() => Task.FromResult(GetValue(PlayerTintIntensityKey, 1.0));

    public Task SetPlayerTintIntensityAsync(double intensity)
    {
        return SetValueAndNotifyAsync(PlayerTintIntensityKey, Math.Clamp(intensity, 0.0, 1.0), 1.0, _ => PlayerDesignSettingsChanged?.Invoke());
    }

    public Task<int> GetSongsPerPageAsync() => Task.FromResult(GetValue(SongsPerPageKey, SettingsDefaults.DefaultSongsPerPage));

    public Task SetSongsPerPageAsync(int songsPerPage)
    {
        return SetValueAndNotifyAsync(SongsPerPageKey, songsPerPage, SettingsDefaults.DefaultSongsPerPage, SongsPerPageChanged);
    }

    // List position is the default priority.
    private static List<ServiceProviderSetting> GetDefaultServiceProviders(ServiceCategory category)
    {
        return category switch
        {
            ServiceCategory.Lyrics =>
            [
                Provider(ServiceProviderIds.LrcLib, "LRCLIB", Strings.Settings_Provider_LRCLIB_Desc),
                Provider(ServiceProviderIds.NetEase, "NetEase", Strings.Settings_Provider_NetEase_Desc)
            ],
            ServiceCategory.Metadata =>
            [
                Provider(ServiceProviderIds.MusicBrainz, "MusicBrainz", Strings.Settings_Provider_MusicBrainz_Desc),
                Provider(ServiceProviderIds.Wikipedia, "Wikipedia", Strings.Settings_Provider_Wikipedia_Desc),
                Provider(ServiceProviderIds.CoverArtArchive, "Cover Art Archive", Strings.Settings_Provider_CoverArtArchive_Desc),
                Provider(ServiceProviderIds.FanartTv, "Fanart.tv", Strings.Settings_Provider_FanartTv_Desc),
                Provider(ServiceProviderIds.TheAudioDb, "TheAudioDB", Strings.Settings_Provider_TheAudioDB_Desc),
                Provider(ServiceProviderIds.Deezer, "Deezer", Strings.Settings_Provider_Deezer_Desc),
                Provider(ServiceProviderIds.AppleMusic, "Apple Music", Strings.Settings_Provider_AppleMusic_Desc),
                Provider(ServiceProviderIds.LastFm, "Last.fm", Strings.Settings_Provider_LastFm_Desc)
            ],
            _ => []
        };

        ServiceProviderSetting Provider(string id, string displayName, string description) =>
            new() { Id = id, DisplayName = displayName, Category = category, Description = description };
    }
}
