using Microsoft.Extensions.Logging;
using Nagi.Core.Artwork;
using Nagi.Core.Metadata;
using Nagi.Core.Platform;

namespace Nagi.Core.Library;

/// <summary>
///     Finds the files beside a song on disk: folder cover art, sidecar lyrics and artist images. It lists each directory once, so a
///     pass over many songs is cheap; it is not thread-safe, so each pass makes its own.
/// </summary>
internal sealed class MediaFileFinder(IFileSystemService fileSystem, ILogger logger)
{
    private readonly Dictionary<string, string?> _coverArtByDirectory = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string?> _artistImageByDirectory = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string Directory, string Pattern), Dictionary<string, string>> _filesByName = new();

    /// <summary>
    ///     The cover art (cover.jpg, folder.png and the like) nearest the song: in its directory, else in the
    ///     directories above it up to and including <paramref name="rootPath" />.
    /// </summary>
    public string? FindCoverArt(string songFilePath, string? rootPath)
    {
        var normalizedRoot = rootPath is null ? null : PathCanonicalizer.NormalizeDirectoryPath(rootPath);
        var directory = Path.GetDirectoryName(songFilePath);
        while (!string.IsNullOrEmpty(directory))
        {
            if (!_coverArtByDirectory.TryGetValue(directory, out var coverArt))
            {
                coverArt = FindCoverArtInDirectory(directory);
                _coverArtByDirectory.Add(directory, coverArt);
            }

            if (coverArt is not null) return coverArt;
            if (normalizedRoot is not null &&
                PathCanonicalizer.NormalizeDirectoryPath(directory).Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase))
                return null;

            directory = Path.GetDirectoryName(directory);
        }

        return null;
    }

    /// <summary>
    ///     Stores the image file as a cached cover with its theme colours, or returns nothing when it can't be read.
    /// </summary>
    public async Task<(string? Uri, string? LightSwatchId, string? DarkSwatchId)> SaveCoverArtAsync(string coverArtPath,
        IImageProcessor imageProcessor)
    {
        try
        {
            var imageBytes = await fileSystem.ReadAllBytesAsync(coverArtPath).ConfigureAwait(false);
            if (imageBytes.Length > 0) return await imageProcessor.SaveCoverArtAndExtractColorsAsync(imageBytes).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to process cover art file {CoverArtPath}.", coverArtPath);
        }

        return default;
    }

    /// <summary>
    ///     The .lrc file, else the .txt file, beside the song with the song's file name.
    /// </summary>
    public string? FindSidecarLyrics(string songFilePath)
    {
        var directory = Path.GetDirectoryName(songFilePath);
        if (string.IsNullOrEmpty(directory)) return null;

        var name = Path.GetFileNameWithoutExtension(songFilePath);
        return FilesByName(directory, "*.lrc").GetValueOrDefault(name) ?? FilesByName(directory, "*.txt").GetValueOrDefault(name);
    }

    /// <summary>The image named "artist" in the directory, if it has one.</summary>
    public string? FindArtistImage(string directory)
    {
        if (_artistImageByDirectory.TryGetValue(directory, out var image)) return image;

        try
        {
            image = fileSystem.GetFiles(directory, "*.*").FirstOrDefault(path =>
                FileExtensions.ImageFileExtensions.Contains(Path.GetExtension(path)) &&
                Path.GetFileNameWithoutExtension(path).Equals(FileExtensions.ArtistImageFileName, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to enumerate artist images in directory '{Directory}'.", directory);
        }

        _artistImageByDirectory.Add(directory, image);
        return image;
    }

    private Dictionary<string, string> FilesByName(string directory, string pattern)
    {
        if (_filesByName.TryGetValue((directory, pattern), out var files)) return files;

        files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var path in fileSystem.GetFiles(directory, pattern))
                files.TryAdd(Path.GetFileNameWithoutExtension(path), path);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to list {Pattern} files in '{Directory}'.", pattern, directory);
        }

        _filesByName.Add((directory, pattern), files);
        return files;
    }

    private string? FindCoverArtInDirectory(string directory)
    {
        try
        {
            return fileSystem.GetFiles(directory, "*.*")
                .Where(path => !fileSystem.IsHiddenOrSystemFile(path) &&
                               FileExtensions.ImageFileExtensions.Contains(Path.GetExtension(path)))
                .Select(path => (Path: path, Priority: FileExtensions.GetCoverArtPriority(Path.GetFileNameWithoutExtension(path))))
                .Where(candidate => candidate.Priority < int.MaxValue)
                .OrderBy(candidate => candidate.Priority)
                .FirstOrDefault().Path;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to enumerate cover art in directory '{Directory}'.", directory);
            return null;
        }
    }
}
