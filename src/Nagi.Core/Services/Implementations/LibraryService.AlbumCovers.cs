using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;

namespace Nagi.Core.Services.Implementations;

/// <summary>
///     Online covers for albums that have no art of their own.
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

    private sealed record AlbumCoverCandidate(Guid Id, string Title, string ArtistName, string? ReleaseId, string? ReleaseGroupId);
}
