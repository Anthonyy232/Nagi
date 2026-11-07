using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nagi.Core.Helpers;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;

namespace Nagi.Core.Services.Helpers;

/// <summary>
///     The naming and cover rules that playlists and smart playlists share.
/// </summary>
internal sealed class PlaylistRules(
    IFileSystemService fileSystem,
    IImageProcessor imageProcessor,
    IPathConfiguration pathConfig,
    ILogger logger)
{
    /// <exception cref="ArgumentException">The name is blank.</exception>
    public static string NormalizeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException(Resources.Strings.Error_PlaylistNameEmpty, nameof(name));
        return ArtistNameHelper.NormalizeStringCore(name) ?? name;
    }

    /// <param name="otherNames">The names the playlist must not share, which excludes its own.</param>
    /// <exception cref="PlaylistNameTakenException">The name is in <paramref name="otherNames" />.</exception>
    public static async Task ThrowIfNameTakenAsync(IQueryable<string> otherNames, string name)
    {
        // Names are NOCASE-collated, so this matches the unique index rather than waiting for SaveChanges to fail.
        if (await otherNames.AnyAsync(other => other == name).ConfigureAwait(false))
            throw new PlaylistNameTakenException(name);
    }

    /// <summary>
    ///     The normalized name, or the first of "Name (2)", "Name (3)"… that is not in <paramref name="takenNames" />.
    /// </summary>
    public static async Task<string> FindFreeNameAsync(IQueryable<string> takenNames, string name)
    {
        var baseName = NormalizeName(name);
        var candidate = baseName;
        for (var suffix = 2; await takenNames.AnyAsync(taken => taken == candidate).ConfigureAwait(false); suffix++)
            candidate = $"{baseName} ({suffix})";
        return candidate;
    }

    /// <summary>
    ///     Caches <paramref name="sourcePath" /> as the playlist's cover, or deletes the cover when it is null or empty.
    /// </summary>
    /// <returns>
    ///     Whether the cover was replaced (false when the image is missing or couldn't be saved, leaving the old one),
    ///     and the new cover's path.
    /// </returns>
    public async Task<(bool Replaced, string? CoverImageUri)> ReplaceCoverAsync(Guid playlistId, string? sourcePath)
    {
        if (string.IsNullOrEmpty(sourcePath))
        {
            DeleteCover(playlistId);
            return (true, null);
        }

        if (!fileSystem.FileExists(sourcePath)) return (false, null);

        var originalBytes = await fileSystem.ReadAllBytesAsync(sourcePath).ConfigureAwait(false);
        var processedBytes = await imageProcessor.ProcessImageBytesAsync(originalBytes).ConfigureAwait(false);
        var savedPath = await ImageStorageHelper.SaveImageBytesAsync(fileSystem, pathConfig.PlaylistImageCachePath,
            playlistId.ToString(), CachedImageKind.Custom, processedBytes).ConfigureAwait(false);
        return (savedPath is not null, savedPath);
    }

    private void DeleteCover(Guid playlistId) =>
        ImageStorageHelper.DeleteImage(fileSystem, pathConfig.PlaylistImageCachePath, playlistId.ToString(), CachedImageKind.Custom);

    /// <summary>
    ///     Deletes a removed playlist's cover. The playlist is already gone, so a failure is only logged.
    /// </summary>
    public void DeleteCoverOfRemovedPlaylist(Guid playlistId)
    {
        try
        {
            DeleteCover(playlistId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to delete cached cover for deleted playlist {PlaylistId}.", playlistId);
        }
    }
}
