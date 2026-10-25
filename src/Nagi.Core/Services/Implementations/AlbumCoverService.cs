using Microsoft.Extensions.Logging;
using Nagi.Core.Helpers;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;

namespace Nagi.Core.Services.Implementations;

public class AlbumCoverService : IAlbumCoverService
{
    /// <summary>The sources <see cref="FindCoverUrlAsync" /> can ask; keep in step with its switch.</summary>
    public static readonly IReadOnlySet<string> SourceIds = new HashSet<string>
    {
        ServiceProviderIds.CoverArtArchive, ServiceProviderIds.AppleMusic, ServiceProviderIds.Deezer, ServiceProviderIds.LastFm
    };

    private readonly ICoverArtArchiveService _coverArtArchive;
    private readonly IMusicBrainzService _musicBrainz;
    private readonly IAppleMusicService _appleMusic;
    private readonly IDeezerService _deezer;
    private readonly ILastFmMetadataService _lastFm;
    private readonly ILogger<AlbumCoverService> _logger;

    public AlbumCoverService(
        ICoverArtArchiveService coverArtArchive,
        IMusicBrainzService musicBrainz,
        IAppleMusicService appleMusic,
        IDeezerService deezer,
        ILastFmMetadataService lastFm,
        ILogger<AlbumCoverService> logger)
    {
        _coverArtArchive = coverArtArchive;
        _musicBrainz = musicBrainz;
        _appleMusic = appleMusic;
        _deezer = deezer;
        _lastFm = lastFm;
        _logger = logger;
    }

    public async Task<ServiceResult<string>> FindCoverUrlAsync(AlbumCoverQuery album,
        IReadOnlyList<ServiceProviderSetting> enabledProviders, CancellationToken cancellationToken = default)
    {
        // A name search for an unknown title or artist would return some unrelated album.
        var canSearchByName = album.Title != Album.UnknownAlbumName && album.ArtistName != Artist.UnknownArtistName;
        var canSearchMusicBrainz = canSearchByName && enabledProviders.Any(p => p.Id == ServiceProviderIds.MusicBrainz);
        var allConclusive = true;

        foreach (var provider in enabledProviders)
        {
            var result = provider.Id switch
            {
                ServiceProviderIds.CoverArtArchive =>
                    await FindInCoverArtArchiveAsync(album, canSearchMusicBrainz, cancellationToken).ConfigureAwait(false),
                ServiceProviderIds.AppleMusic when canSearchByName =>
                    await _appleMusic.GetAlbumCoverUrlAsync(album.Title, album.ArtistName, cancellationToken).ConfigureAwait(false),
                ServiceProviderIds.Deezer when canSearchByName =>
                    await _deezer.GetAlbumCoverUrlAsync(album.Title, album.ArtistName, cancellationToken).ConfigureAwait(false),
                ServiceProviderIds.LastFm when canSearchByName =>
                    await _lastFm.GetAlbumCoverUrlAsync(album.Title, album.ArtistName, cancellationToken).ConfigureAwait(false),
                _ => null
            };
            if (result is null) continue;

            if (result.Data is not null)
            {
                _logger.LogDebug("Using {Provider} cover for album '{AlbumTitle}'.", provider.Id, album.Title);
                return result;
            }

            allConclusive &= result.IsConclusive;
        }

        return allConclusive ? ServiceResult<string>.FromSuccessNotFound() : ServiceResult<string>.FromTemporaryError();
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
