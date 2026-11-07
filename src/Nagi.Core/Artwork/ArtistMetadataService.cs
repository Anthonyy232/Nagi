using System.Globalization;
using Microsoft.Extensions.Logging;
using Nagi.Core.OnlineProviders;
using Nagi.Core.Settings;

namespace Nagi.Core.Artwork;

public class ArtistMetadataService : IArtistMetadataService, IDisposable
{
    private readonly OnlineLookupCancellation _onlineLookups = new();
    private readonly ILastFmMetadataService _lastFmService;
    private readonly IMusicBrainzService _musicBrainzService;
    private readonly IFanartTvService _fanartTvService;
    private readonly ITheAudioDbService _theAudioDbService;
    private readonly IDeezerService _deezerService;
    private readonly IWikipediaService _wikipediaService;
    private readonly ISettingsService _settingsService;
    private readonly ILogger<ArtistMetadataService> _logger;

    public ArtistMetadataService(
        ILastFmMetadataService lastFmService,
        IMusicBrainzService musicBrainzService,
        IFanartTvService fanartTvService,
        ITheAudioDbService theAudioDbService,
        IDeezerService deezerService,
        IWikipediaService wikipediaService,
        ISettingsService settingsService,
        ILogger<ArtistMetadataService> logger)
    {
        _lastFmService = lastFmService;
        _musicBrainzService = musicBrainzService;
        _fanartTvService = fanartTvService;
        _theAudioDbService = theAudioDbService;
        _deezerService = deezerService;
        _wikipediaService = wikipediaService;
        _settingsService = settingsService;
        _logger = logger;
        _settingsService.SettingChanged += OnSettingChanged;
    }

    public async Task<ArtistMetadataResult?> FindAsync(string artistName, string? musicBrainzId,
        CancellationToken cancellationToken = default)
    {
        var turnedOff = _onlineLookups.Token;
        if (!_settingsService.Get(CoreSettings.FetchOnlineMetadata)) return null;
        var enabledProviders = _settingsService.Get(CoreSettings.MetadataProviders).Where(p => p.IsEnabled).ToList();
        if (enabledProviders.Count == 0) return null;

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, turnedOff);
        try
        {
            return await AskSourcesAsync(artistName, musicBrainzId, enabledProviders, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    public void Dispose()
    {
        _settingsService.SettingChanged -= OnSettingChanged;
        _onlineLookups.CancelRunning();
    }

    private async Task<ArtistMetadataResult> AskSourcesAsync(string artistName, string? musicBrainzId,
        IReadOnlyList<ServiceProviderSetting> enabledProviders, CancellationToken cancellationToken)
    {
        _logger.LogDebug("Using metadata providers for '{ArtistName}': {Providers}",
            artistName, string.Join(", ", enabledProviders.Select(p => p.Id)));

        var enabledIds = enabledProviders.Select(p => p.Id).ToHashSet();
        var language = CultureInfo.CurrentCulture.TwoLetterISOLanguageName;
        var lookups = new Dictionary<string, Task<ServiceResult<ArtistInfo>>>();

        var musicBrainzLookup = string.IsNullOrEmpty(musicBrainzId) && enabledIds.Contains(ServiceProviderIds.MusicBrainz)
            ? _musicBrainzService.SearchArtistAsync(artistName, cancellationToken)
            : null;

        // Name-based providers can start before MusicBrainz resolves the ID the others need.
        if (enabledIds.Contains(ServiceProviderIds.LastFm))
            lookups[ServiceProviderIds.LastFm] = _lastFmService.GetArtistInfoAsync(artistName, language, cancellationToken);
        if (enabledIds.Contains(ServiceProviderIds.Deezer))
            lookups[ServiceProviderIds.Deezer] = MapAsync(_deezerService.GetArtistImageUrlAsync(artistName, cancellationToken),
                url => new ArtistInfo { ImageUrl = url });

        var mbid = musicBrainzId;
        string? resolvedMbid = null;
        var musicBrainzAnswered = true;
        if (musicBrainzLookup is not null)
        {
            var result = await musicBrainzLookup.ConfigureAwait(false);
            musicBrainzAnswered = result.IsConclusive;
            if (!string.IsNullOrEmpty(result.Data))
            {
                mbid = resolvedMbid = result.Data;
                _logger.LogInformation("Resolved MusicBrainz ID {MBID} for artist '{ArtistName}'", mbid, artistName);
            }
        }

        if (!string.IsNullOrEmpty(mbid))
        {
            if (enabledIds.Contains(ServiceProviderIds.TheAudioDb))
                lookups[ServiceProviderIds.TheAudioDb] = MapAsync(
                    _theAudioDbService.GetArtistMetadataAsync(mbid, language, cancellationToken),
                    // A square thumbnail suits the round artist frame better than landscape fanart.
                    info => new ArtistInfo { ImageUrl = info.ThumbUrl ?? info.FanartUrl, Biography = info.Biography });
            if (enabledIds.Contains(ServiceProviderIds.FanartTv))
                lookups[ServiceProviderIds.FanartTv] = MapAsync(_fanartTvService.GetArtistImagesAsync(mbid, cancellationToken),
                    images => new ArtistInfo { ImageUrl = images.ThumbUrl ?? images.BackgroundUrl });
            if (enabledIds.Contains(ServiceProviderIds.Wikipedia))
                lookups[ServiceProviderIds.Wikipedia] = MapAsync(
                    _wikipediaService.GetArtistBiographyAsync(mbid, language, cancellationToken),
                    biography => new ArtistInfo { Biography = biography });
        }

        string? imageUrl = null;
        string? biography = null;
        var isFinal = await ProviderPriority.TakeInOrderAsync(
            enabledProviders.Where(p => lookups.ContainsKey(p.Id)).Select(p => lookups[p.Id]),
            info =>
            {
                if (string.IsNullOrEmpty(imageUrl)) imageUrl = info.ImageUrl;
                if (string.IsNullOrEmpty(biography)) biography = info.Biography;
                return !string.IsNullOrEmpty(imageUrl) && !string.IsNullOrEmpty(biography);
            }, cancellationToken).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();
        return new ArtistMetadataResult(resolvedMbid, imageUrl, biography, musicBrainzAnswered && isFinal);
    }

    private void OnSettingChanged(Setting setting)
    {
        if (setting == CoreSettings.FetchOnlineMetadata && !_settingsService.Get(CoreSettings.FetchOnlineMetadata))
            _onlineLookups.CancelRunning();
    }

    private static async Task<ServiceResult<ArtistInfo>> MapAsync<T>(Task<ServiceResult<T>> lookup, Func<T, ArtistInfo> map)
        where T : class =>
        (await lookup.ConfigureAwait(false)).Map(map);
}
