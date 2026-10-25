using System.Globalization;
using Microsoft.Extensions.Logging;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;

namespace Nagi.Core.Services.Implementations;

public class ArtistMetadataService : IArtistMetadataService
{
    private readonly ILastFmMetadataService _lastFmService;
    private readonly IMusicBrainzService _musicBrainzService;
    private readonly IFanartTvService _fanartTvService;
    private readonly ITheAudioDbService _theAudioDbService;
    private readonly IDeezerService _deezerService;
    private readonly IWikipediaService _wikipediaService;
    private readonly ILogger<ArtistMetadataService> _logger;

    public ArtistMetadataService(
        ILastFmMetadataService lastFmService,
        IMusicBrainzService musicBrainzService,
        IFanartTvService fanartTvService,
        ITheAudioDbService theAudioDbService,
        IDeezerService deezerService,
        IWikipediaService wikipediaService,
        ILogger<ArtistMetadataService> logger)
    {
        _lastFmService = lastFmService;
        _musicBrainzService = musicBrainzService;
        _fanartTvService = fanartTvService;
        _theAudioDbService = theAudioDbService;
        _deezerService = deezerService;
        _wikipediaService = wikipediaService;
        _logger = logger;
    }

    public async Task<ArtistMetadataResult> FindAsync(string artistName, string? musicBrainzId,
        IReadOnlyList<ServiceProviderSetting> enabledProviders, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Using metadata providers for '{ArtistName}': {Providers}",
            artistName, string.Join(", ", enabledProviders.Select(p => p.Id)));

        var enabledIds = enabledProviders.Select(p => p.Id).ToHashSet();
        var tasks = new Dictionary<string, Task<(bool IsConclusive, string? ImageUrl, string? Biography)>>();

        var mbid = musicBrainzId;
        string? resolvedMbid = null;
        Task<ServiceResult<string>>? musicBrainzTask = null;
        if (string.IsNullOrEmpty(mbid) && enabledIds.Contains(ServiceProviderIds.MusicBrainz))
            musicBrainzTask = _musicBrainzService.SearchArtistAsync(artistName, cancellationToken);

        // Name-based providers can start before MusicBrainz resolves the ID needed by the others.
        if (enabledIds.Contains(ServiceProviderIds.LastFm))
            tasks[ServiceProviderIds.LastFm] = FetchFromLastFmAsync(artistName, cancellationToken);
        if (enabledIds.Contains(ServiceProviderIds.Deezer))
            tasks[ServiceProviderIds.Deezer] = FetchFromDeezerAsync(artistName, cancellationToken);

        var anyProviderConclusive = false;
        var allProvidersConclusive = true;

        string? finalImageUrl = null;
        string? finalBiography = null;
        try
        {
            if (musicBrainzTask != null)
            {
                try
                {
                    var result = await musicBrainzTask.ConfigureAwait(false);
                    anyProviderConclusive = result.IsConclusive;
                    allProvidersConclusive = result.IsConclusive;
                    if (!string.IsNullOrEmpty(result.Data))
                    {
                        mbid = resolvedMbid = result.Data;
                        _logger.LogInformation("Resolved MusicBrainz ID {MBID} for artist '{ArtistName}'", mbid, artistName);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    allProvidersConclusive = false;
                    _logger.LogDebug(ex, "MusicBrainz lookup failed for '{ArtistName}'.", artistName);
                }
            }

            if (!string.IsNullOrEmpty(mbid))
            {
                if (enabledIds.Contains(ServiceProviderIds.TheAudioDb))
                    tasks[ServiceProviderIds.TheAudioDb] = FetchFromTheAudioDbAsync(mbid, cancellationToken);
                if (enabledIds.Contains(ServiceProviderIds.FanartTv))
                    tasks[ServiceProviderIds.FanartTv] = FetchFromFanartTvAsync(mbid, cancellationToken);
                if (enabledIds.Contains(ServiceProviderIds.Wikipedia))
                    tasks[ServiceProviderIds.Wikipedia] = FetchFromWikipediaAsync(mbid, cancellationToken);
            }

            foreach (var provider in enabledProviders)
            {
                if (!tasks.TryGetValue(provider.Id, out var task)) continue;

                try
                {
                    // Sequential await in priority order. If a higher priority task is still running,
                    // we wait for it. If it finishes and has data, we break early and ignore lower priority ones.
                    var (isConclusive, imageUrl, biography) = await task.ConfigureAwait(false);
                    anyProviderConclusive |= isConclusive;
                    allProvidersConclusive &= isConclusive;

                    if (string.IsNullOrEmpty(finalImageUrl) && !string.IsNullOrEmpty(imageUrl))
                    {
                        finalImageUrl = imageUrl;
                        _logger.LogDebug("Using image from {Provider} for artist '{ArtistName}'.", provider.DisplayName, artistName);
                    }

                    if (string.IsNullOrEmpty(finalBiography) && !string.IsNullOrEmpty(biography))
                    {
                        finalBiography = biography;
                        _logger.LogDebug("Using biography from {Provider} for artist '{ArtistName}'.", provider.DisplayName, artistName);
                    }

                    if (!string.IsNullOrEmpty(finalImageUrl) && !string.IsNullOrEmpty(finalBiography))
                        break;
                }
                catch (OperationCanceledException)
                {
                    allProvidersConclusive = false;
                    if (cancellationToken.IsCancellationRequested) break;
                }
                catch (Exception ex)
                {
                    allProvidersConclusive = false;
                    _logger.LogDebug(ex, "Metadata provider {Provider} failed for '{ArtistName}'.", provider.DisplayName, artistName);
                }
            }
        }
        finally
        {
            // Observe unfinished providers even when cancellation interrupts the identity lookup.
            foreach (var task in tasks.Values)
            {
                _ = task.ContinueWith(
                    static (t, state) => ((ILogger<ArtistMetadataService>)state!).LogDebug(
                        t.Exception!.InnerException, "Metadata provider task faulted after lookup stopped waiting"),
                    _logger,
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new ArtistMetadataResult(resolvedMbid, finalImageUrl, finalBiography,
            anyProviderConclusive && allProvidersConclusive);
    }

    private async Task<(bool IsConclusive, string? ImageUrl, string? Biography)> FetchFromTheAudioDbAsync(string mbid, CancellationToken ct)
    {
        var result = await _theAudioDbService.GetArtistMetadataAsync(mbid, CultureInfo.CurrentCulture.TwoLetterISOLanguageName, ct).ConfigureAwait(false);
        if (result.Status == ServiceResultStatus.Success && result.Data is not null)
        {
            // Prefer square thumbnail over landscape fanart for round frame display
            var url = result.Data.ThumbUrl ?? result.Data.FanartUrl;
            return (true, url, result.Data.Biography);
        }
        return (result.IsConclusive, null, null);
    }

    private async Task<(bool IsConclusive, string? ImageUrl, string? Biography)> FetchFromFanartTvAsync(string mbid, CancellationToken ct)
    {
        var result = await _fanartTvService.GetArtistImagesAsync(mbid, ct).ConfigureAwait(false);
        if (result.Status == ServiceResultStatus.Success && result.Data is not null)
        {
            // Prefer square thumbnail over landscape background for round frame display
            var url = result.Data.ThumbUrl ?? result.Data.BackgroundUrl;
            return (true, url, null);
        }
        return (result.IsConclusive, null, null);
    }

    private async Task<(bool IsConclusive, string? ImageUrl, string? Biography)> FetchFromLastFmAsync(string artistName, CancellationToken ct)
    {
        var result = await _lastFmService.GetArtistInfoAsync(artistName, CultureInfo.CurrentCulture.TwoLetterISOLanguageName, ct).ConfigureAwait(false);
        if (result.Status == ServiceResultStatus.Success && result.Data is not null)
        {
            return (true, result.Data.ImageUrl, result.Data.Biography);
        }
        return (result.IsConclusive, null, null);
    }

    private async Task<(bool IsConclusive, string? ImageUrl, string? Biography)> FetchFromWikipediaAsync(string mbid, CancellationToken ct)
    {
        var result = await _wikipediaService.GetArtistBiographyAsync(mbid, CultureInfo.CurrentCulture.TwoLetterISOLanguageName, ct).ConfigureAwait(false);
        return (result.IsConclusive, null, result.Data);
    }

    private async Task<(bool IsConclusive, string? ImageUrl, string? Biography)> FetchFromDeezerAsync(string artistName, CancellationToken ct)
    {
        var result = await _deezerService.GetArtistImageUrlAsync(artistName, ct).ConfigureAwait(false);
        return (result.IsConclusive, result.Data, null);
    }
}
