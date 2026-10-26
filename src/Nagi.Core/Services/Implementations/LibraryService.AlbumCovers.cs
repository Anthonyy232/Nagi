using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;

namespace Nagi.Core.Services.Implementations;

/// <summary>
///     Album covers: online covers for albums that have no art, keeping album covers in step with their tracks, and
///     the album cover for tracks without their own.
/// </summary>
public partial class LibraryService
{
    private async Task FetchMissingAlbumCoversAsync(IReadOnlyList<ServiceProviderSetting> enabledProviders,
        CancellationToken token)
    {
        if (!enabledProviders.Any(p => AlbumCoverService.SourceIds.Contains(p.Id))) return;

        const int batchSize = 50;
        // "Unknown Album" groups unrelated tracks, so no single cover is right for it.
        var unknownAlbum = Album.UnknownAlbumName;
        Guid? afterId = null;
        while (!token.IsCancellationRequested)
        {
            List<AlbumCoverCandidate> albums;
            await using (var context = await _contextFactory.CreateDbContextAsync(token).ConfigureAwait(false))
            {
                var query = context.Albums.Where(a =>
                    a.CoverArtUri == null && a.CoverArtLastCheckedUtc == null && a.Title != unknownAlbum);
                if (afterId.HasValue) query = query.Where(a => a.Id > afterId.Value);
                albums = await query.OrderBy(a => a.Id).Take(batchSize)
                    .Select(a => new AlbumCoverCandidate(a.Id, a.Title, a.PrimaryArtistName,
                        a.Songs.Where(s => s.MusicBrainzReleaseId != null).Select(s => s.MusicBrainzReleaseId).FirstOrDefault(),
                        a.Songs.Where(s => s.MusicBrainzReleaseGroupId != null).Select(s => s.MusicBrainzReleaseGroupId).FirstOrDefault()))
                    .ToListAsync(token).ConfigureAwait(false);
            }

            if (albums.Count == 0) break;
            // A failed lookup stays retryable, but must not loop forever in this run.
            afterId = albums[^1].Id;

            var updatedCount = 0;
            foreach (var album in albums)
            {
                if (token.IsCancellationRequested) break;
                try
                {
                    if (await FetchAlbumCoverAsync(album, enabledProviders, token).ConfigureAwait(false))
                        updatedCount++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Failed to fetch a cover for album {AlbumId} in background.", album.Id);
                }
            }

            if (updatedCount > 0)
            {
                _logger.LogInformation("Added online covers to {Count} albums.", updatedCount);
                LibraryContentChanged?.Invoke(this, new LibraryContentChangedEventArgs(LibraryChangeType.SongsUpdated));
            }
        }
    }

    private async Task<bool> FetchAlbumCoverAsync(AlbumCoverCandidate album,
        IReadOnlyList<ServiceProviderSetting> enabledProviders, CancellationToken token)
    {
        var result = await _albumCoverService.FindCoverUrlAsync(
            new AlbumCoverQuery(album.Title, album.ArtistName, album.ReleaseId, album.ReleaseGroupId),
            enabledProviders, token).ConfigureAwait(false);

        (string? Uri, string? LightSwatchId, string? DarkSwatchId) cover = default;
        if (result.Data is not null)
        {
            var imageBytes = await DownloadImageBytesAsync(new Uri(result.Data), token).ConfigureAwait(false);
            if (imageBytes is not null)
                cover = await _imageProcessor.SaveCoverArtAndExtractColorsAsync(imageBytes).ConfigureAwait(false);
        }

        // A cover that failed to download stays retryable, like a source that failed.
        if (cover.Uri is null && (result.Data is not null || !result.IsConclusive)) return false;

        await using var context = await _contextFactory.CreateDbContextAsync(token).ConfigureAwait(false);
        var checkedAt = DateTime.UtcNow;
        // Art a scan found in the meantime wins, so only an album still without a cover takes the online one.
        var stored = cover.Uri is not null && await context.Albums.Where(a => a.Id == album.Id && a.CoverArtUri == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(a => a.CoverArtUri, cover.Uri)
                .SetProperty(a => a.CoverArtLastCheckedUtc, checkedAt), token)
            .ConfigureAwait(false) > 0;
        if (!stored)
        {
            await context.Albums.Where(a => a.Id == album.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(a => a.CoverArtLastCheckedUtc, checkedAt), token)
                .ConfigureAwait(false);
            return false;
        }

        await context.Songs.Where(s => s.AlbumId == album.Id && s.AlbumArtUriFromTrack == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(s => s.AlbumArtUriFromTrack, cover.Uri)
                .SetProperty(s => s.LightSwatchId, cover.LightSwatchId)
                .SetProperty(s => s.DarkSwatchId, cover.DarkSwatchId), token)
            .ConfigureAwait(false);
        return true;
    }

    /// <summary>
    ///     Gives each album whose cover none of its tracks use the art of its first track that has some. Without such
    ///     art, a cover whose cached file is missing is cleared so the online pass looks for one again.
    /// </summary>
    /// <returns>The number of albums updated.</returns>
    private async Task<int> SyncAlbumCoversWithTracksAsync(Guid folderId, CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        // A cover a track uses is current; if its file is missing, scanning the folder of that track recreates it.
        var albums = await context.Albums
            .Where(a => a.Songs.Any(s => s.FolderId == folderId) &&
                        (a.CoverArtUri == null || !a.Songs.Any(s => s.AlbumArtUriFromTrack == a.CoverArtUri)))
            .Select(a => new
            {
                a.Id,
                a.CoverArtUri,
                TrackArt = a.Songs
                    .Where(s => s.AlbumArtUriFromTrack != null)
                    .OrderBy(s => s.DiscNumber).ThenBy(s => s.TrackNumber).ThenBy(s => s.FilePath)
                    .Select(s => s.AlbumArtUriFromTrack)
                    .FirstOrDefault()
            })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var updatedCount = 0;
        foreach (var album in albums)
        {
            // A track re-read without art of its own drops the album cover it showed, which doesn't make that cover
            // out of date.
            if (album.TrackArt is null && (album.CoverArtUri is null || _fileSystem.FileExists(album.CoverArtUri)))
                continue;

            updatedCount += await context.Albums.Where(a => a.Id == album.Id)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(a => a.CoverArtUri, album.TrackArt)
                    .SetProperty(a => a.CoverArtLastCheckedUtc, (DateTime?)null), cancellationToken)
                .ConfigureAwait(false);
        }

        if (updatedCount > 0)
            _logger.LogInformation("Updated the cover of {Count} albums to match their tracks.", updatedCount);
        return updatedCount;
    }

    /// <summary>
    ///     Gives tracks without embedded or folder art the cover of their album, which may come from another
    ///     track or an online source.
    /// </summary>
    /// <returns>The number of tracks updated.</returns>
    private async Task<int> ApplyAlbumCoversToTracksWithoutArtAsync(Guid folderId, CancellationToken cancellationToken)
    {
        // "Unknown Album" groups unrelated tracks, so one of them must not lend its art to the rest.
        var unknownAlbum = Album.UnknownAlbumName;
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var tracksWithoutArt = context.Songs.Where(s => s.FolderId == folderId && s.AlbumArtUriFromTrack == null &&
                                                        s.Album != null && s.Album.CoverArtUri != null &&
                                                        s.Album.Title != unknownAlbum);
        var covers = await tracksWithoutArt.Select(s => s.Album!.CoverArtUri!).Distinct()
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var updatedCount = 0;
        foreach (var cover in covers)
        {
            // The album's cover came from one of its tracks or the online pass, which stored its theme colors.
            var swatches = await context.Songs.Where(s => s.AlbumArtUriFromTrack == cover)
                .Select(s => new { s.LightSwatchId, s.DarkSwatchId })
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            var lightSwatchId = swatches?.LightSwatchId;
            var darkSwatchId = swatches?.DarkSwatchId;

            updatedCount += await tracksWithoutArt.Where(s => s.Album!.CoverArtUri == cover)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(s => s.AlbumArtUriFromTrack, cover)
                    .SetProperty(s => s.LightSwatchId, lightSwatchId)
                    .SetProperty(s => s.DarkSwatchId, darkSwatchId), cancellationToken)
                .ConfigureAwait(false);
        }

        if (updatedCount > 0)
            _logger.LogInformation("Gave {Count} tracks without art their album cover.", updatedCount);
        return updatedCount;
    }

    private sealed record AlbumCoverCandidate(Guid Id, string Title, string ArtistName, string? ReleaseId, string? ReleaseGroupId);
}
