using Microsoft.Extensions.Logging;
using Nagi.Core.Helpers;
using Nagi.Core.Http;
using Nagi.Core.Library;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;
using Nagi.Core.Services.Helpers;
using Nagi.Core.Settings;

namespace Nagi.Core.Services.Implementations;

public class AlbumCoverService : IAlbumCoverService, IDisposable
{
    private readonly OnlineLookupCancellation _onlineLookups = new();
    private readonly ICoverArtArchiveService _coverArtArchive;
    private readonly IMusicBrainzService _musicBrainz;
    private readonly IAppleMusicService _appleMusic;
    private readonly IDeezerService _deezer;
    private readonly ILastFmMetadataService _lastFm;
    private readonly ISettingsService _settingsService;
    private readonly ILogger<AlbumCoverService> _logger;

    public AlbumCoverService(
        ICoverArtArchiveService coverArtArchive,
        IMusicBrainzService musicBrainz,
        IAppleMusicService appleMusic,
        IDeezerService deezer,
        ILastFmMetadataService lastFm,
        ISettingsService settingsService,
        ILogger<AlbumCoverService> logger)
    {
        _coverArtArchive = coverArtArchive;
        _musicBrainz = musicBrainz;
        _appleMusic = appleMusic;
        _deezer = deezer;
        _lastFm = lastFm;
        _settingsService = settingsService;
        _logger = logger;
        _settingsService.SettingChanged += OnSettingChanged;
    }

    public async Task<ServiceResult<string>?> FindCoverUrlAsync(AlbumCoverQuery album,
        CancellationToken cancellationToken = default)
    {
        var turnedOff = _onlineLookups.Token;
        if (!_settingsService.Get(CoreSettings.FetchOnlineMetadata)) return null;
        var enabledProviders = _settingsService.Get(CoreSettings.MetadataProviders).Where(p => p.IsEnabled).ToList();
        if (!enabledProviders.Any(p => ProviderCatalog.Can(p.Id, ProviderCapabilities.AlbumCover))) return null;

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, turnedOff);
        try
        {
            return await AskSourcesAsync(album, enabledProviders, linked.Token).ConfigureAwait(false);
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

    private async Task<ServiceResult<string>> AskSourcesAsync(AlbumCoverQuery album,
        IReadOnlyList<ServiceProviderSetting> enabledProviders, CancellationToken cancellationToken)
    {
        // A name search for an unknown title or artist would return some unrelated album.
        var canSearchByName = album.Title != Album.UnknownAlbumName && album.ArtistName != Artist.UnknownArtistName;
        var canSearchMusicBrainz = canSearchByName && enabledProviders.Any(p => p.Id == ServiceProviderIds.MusicBrainz);

        string? coverUrl = null;
        // Enumerated lazily, so the sources are asked one at a time and the search stops at the first cover.
        var isFinal = await ProviderPriority.TakeInOrderAsync(
            enabledProviders
                .Where(p => ProviderCatalog.Can(p.Id, ProviderCapabilities.AlbumCover) &&
                            (canSearchByName || p.Id == ServiceProviderIds.CoverArtArchive))
                .Select(p => AskSourceAsync(p.Id, album, canSearchMusicBrainz, cancellationToken)),
            url =>
            {
                coverUrl = url;
                return true;
            }, cancellationToken).ConfigureAwait(false);

        return coverUrl is not null ? ServiceResult<string>.FromSuccess(coverUrl)
            : isFinal ? ServiceResult<string>.FromSuccessNotFound()
            : ServiceResult<string>.FromTemporaryError();
    }

    private async Task<ServiceResult<string>> AskSourceAsync(string providerId, AlbumCoverQuery album,
        bool canSearchMusicBrainz, CancellationToken cancellationToken)
    {
        var result = await (providerId switch
        {
            ServiceProviderIds.CoverArtArchive => FindInCoverArtArchiveAsync(album, canSearchMusicBrainz, cancellationToken),
            ServiceProviderIds.AppleMusic => _appleMusic.GetAlbumCoverUrlAsync(album.Title, album.ArtistName, cancellationToken),
            ServiceProviderIds.Deezer => _deezer.GetAlbumCoverUrlAsync(album.Title, album.ArtistName, cancellationToken),
            ServiceProviderIds.LastFm => _lastFm.GetAlbumCoverUrlAsync(album.Title, album.ArtistName, cancellationToken),
            _ => throw new NotSupportedException($"No album cover lookup for provider '{providerId}'.")
        }).ConfigureAwait(false);

        if (result.Data is not null)
            _logger.LogDebug("Using {Provider} cover for album '{AlbumTitle}'.", providerId, album.Title);
        return result;
    }

    private void OnSettingChanged(Setting setting)
    {
        if (setting == CoreSettings.FetchOnlineMetadata && !_settingsService.Get(CoreSettings.FetchOnlineMetadata))
            _onlineLookups.CancelRunning();
    }

    private async Task<ServiceResult<string>> FindInCoverArtArchiveAsync(AlbumCoverQuery album, bool canSearchMusicBrainz,
        CancellationToken cancellationToken)
    {
        var conclusive = true;
        if (album.ReleaseId is not null)
        {
            var release = await _coverArtArchive.GetReleaseCoverUrlAsync(album.ReleaseId, cancellationToken).ConfigureAwait(false);
            if (release.Data is not null) return release;
            conclusive = release.IsConclusive;
        }

        // A release without art often shares a cover with another edition of the same release group.
        var releaseGroupId = album.ReleaseGroupId;
        if (releaseGroupId is null && canSearchMusicBrainz)
        {
            // MusicBrainz titles leave out labels like "(Remastered)", which would sink the match score.
            var search = await _musicBrainz.SearchReleaseGroupAsync(NameMatch.WithoutEditionLabel(album.Title),
                album.ArtistName, cancellationToken).ConfigureAwait(false);
            conclusive &= search.IsConclusive;
            releaseGroupId = search.Data;
        }

        if (releaseGroupId is null)
            return conclusive ? ServiceResult<string>.FromSuccessNotFound() : ServiceResult<string>.FromTemporaryError();

        var group = await _coverArtArchive.GetReleaseGroupCoverUrlAsync(releaseGroupId, cancellationToken).ConfigureAwait(false);
        return group.Data is not null || (conclusive && group.IsConclusive)
            ? group
            : ServiceResult<string>.FromTemporaryError();
    }
}
