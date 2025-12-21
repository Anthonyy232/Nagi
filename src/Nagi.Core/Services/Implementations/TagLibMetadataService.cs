using System.Text;
using Microsoft.Extensions.Logging;
using Nagi.Core.Constants;
using Nagi.Core.Helpers;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using TagLib;
using TagLib.Id3v2;
using File = TagLib.File;
using Tag = TagLib.Tag;

namespace Nagi.Core.Services.Implementations;

/// <summary>
///     Extracts music file metadata using the TagLib-Sharp library.
/// </summary>
public class TagLibMetadataService : IMetadataService
{
    private const string UnknownArtistName = "Unknown Artist";
    private const string UnknownAlbumName = "Unknown Album";


    private readonly IFileSystemService _fileSystem;
    private readonly IImageProcessor _imageProcessor;
    private readonly ILogger<TagLibMetadataService> _logger;
    private readonly IPathConfiguration _pathConfig;

    public TagLibMetadataService(IImageProcessor imageProcessor, IFileSystemService fileSystem,
        IPathConfiguration pathConfig, ILogger<TagLibMetadataService> logger)
    {
        _imageProcessor = imageProcessor ?? throw new ArgumentNullException(nameof(imageProcessor));
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _pathConfig = pathConfig ?? throw new ArgumentNullException(nameof(pathConfig));
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<SongFileMetadata> ExtractMetadataAsync(string filePath, string? baseFolderPath = null)
    {
        var metadata = new SongFileMetadata { FilePath = filePath };

        try
        {
            var fileInfo = _fileSystem.GetFileInfo(filePath);
            metadata.FileCreatedDate = fileInfo.CreationTimeUtc;
            metadata.FileModifiedDate = fileInfo.LastWriteTimeUtc;
            metadata.Title = _fileSystem.GetFileNameWithoutExtension(filePath);

            // Use a timeout wrapper for TagLib operations to prevent indefinite hangs
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30)); // 30-second timeout
            var extractionTask = Task.Run(() =>
            {
                // Use a read-only file abstraction for safety and to prevent file locks.
                using var tagFile = File.Create(new NonWritableFileAbstraction(filePath));

                if (tagFile?.Tag is null || tagFile.Properties is null)
                    throw new CorruptFileException("TagLib could not read the file's tag or property structures.");

                return (tagFile.Tag, tagFile.Properties);
            }, cts.Token);

            var (tag, properties) = await extractionTask;

            PopulateMetadataFromTag(metadata, tag, properties);

            // Get cached or extract new synchronized lyrics (after parsing tag so we have artist/title).
            metadata.LrcFilePath = await GetLrcPathAsync(filePath, fileInfo.LastWriteTimeUtc, metadata.Artist, metadata.Title);

            // As a fallback, look for an external .lrc file if no embedded lyrics were found.
            if (string.IsNullOrWhiteSpace(metadata.LrcFilePath)) metadata.LrcFilePath = FindLrcFilePath(filePath);

            // Process album art with a separate timeout to avoid blocking
            using var albumArtCts =
                new CancellationTokenSource(TimeSpan.FromSeconds(15)); // 15-second timeout for album art
            var albumArtTask = Task.Run(() =>
            {
                using var tagFileForArt = File.Create(new NonWritableFileAbstraction(filePath));
                return tagFileForArt?.Tag;
            }, albumArtCts.Token);

            try
            {
                var tagForArt = await albumArtTask;
                if (tagForArt != null) await ProcessAlbumArtAsync(metadata, tagForArt, baseFolderPath);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Album art extraction timed out for file: {FilePath}", filePath);
                // Continue without album art rather than failing completely
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Metadata extraction timed out for file: {FilePath}", filePath);
            metadata.ExtractionFailed = true;
            metadata.ErrorMessage = "ExtractionTimeout";
        }
        catch (CorruptFileException ex)
        {
            _logger.LogWarning(ex, "Corrupt file detected while extracting metadata from '{FilePath}'.", filePath);
            metadata.ExtractionFailed = true;
            metadata.ErrorMessage = "CorruptFile";
        }
        catch (UnsupportedFormatException)
        {
            _logger.LogWarning("Unsupported file format for metadata extraction: '{FilePath}'.", filePath);
            metadata.ExtractionFailed = true;
            metadata.ErrorMessage = "UnsupportedFormat";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An unexpected error occurred during metadata extraction for '{FilePath}'.",
                filePath);
            metadata.ExtractionFailed = true;
            metadata.ErrorMessage = ex.GetType().Name;
        }

        return metadata;
    }

    /// <summary>
    ///     Populates the metadata object from the file's tags and properties, providing sane defaults for missing values.
    /// </summary>
    private void PopulateMetadataFromTag(SongFileMetadata metadata, Tag tag, Properties properties)
    {
        var artist = SanitizeString(tag.Performers?.FirstOrDefault()) ?? UnknownArtistName;
        var albumArtist = SanitizeString(tag.AlbumArtists?.FirstOrDefault()) ?? artist;
        var album = SanitizeString(tag.Album) ?? UnknownAlbumName;

        metadata.Title = SanitizeString(tag.Title) ?? metadata.Title;
        metadata.Artist = artist;
        metadata.Album = album;
        metadata.AlbumArtist = albumArtist;
        metadata.Duration = properties.Duration;
        metadata.Year = tag.Year > 0 ? (int)tag.Year : null;
        metadata.TrackNumber = tag.Track > 0 ? (int)tag.Track : null;
        metadata.TrackCount = tag.TrackCount > 0 ? (int)tag.TrackCount : null;
        metadata.DiscNumber = tag.Disc > 0 ? (int)tag.Disc : null;
        metadata.DiscCount = tag.DiscCount > 0 ? (int)tag.DiscCount : null;
        metadata.Bpm = tag.BeatsPerMinute > 0 ? tag.BeatsPerMinute : null;
        metadata.SampleRate = properties.AudioSampleRate > 0 ? properties.AudioSampleRate : null;
        metadata.Bitrate = properties.AudioBitrate > 0 ? properties.AudioBitrate : null;
        metadata.Channels = properties.AudioChannels > 0 ? properties.AudioChannels : null;
        metadata.Lyrics = SanitizeString(tag.Lyrics);
        metadata.Composer = SanitizeString(tag.Composers?.FirstOrDefault());
        metadata.Grouping = SanitizeString(tag.Grouping);
        metadata.Copyright = SanitizeString(tag.Copyright);
        metadata.Comment = SanitizeString(tag.Comment);
        metadata.Conductor = SanitizeString(tag.Conductor);
        metadata.MusicBrainzTrackId = SanitizeString(tag.MusicBrainzTrackId);
        metadata.MusicBrainzReleaseId = SanitizeString(tag.MusicBrainzReleaseId);
        metadata.Genres = tag.Genres?
            .Select(SanitizeString)
            .OfType<string>() // Filters out nulls from the sequence.
            .ToList() ?? [];
    }

    /// <summary>
    ///     Extracts album art from the tag and saves it to the cache. Each song gets its own
    ///     cached cover art file, keyed by the song's file path. If no embedded art is found,
    ///     falls back to searching directory hierarchy for common cover art files.
    /// </summary>
    private async Task ProcessAlbumArtAsync(SongFileMetadata metadata, Tag tag, string? baseFolderPath)
    {
        var picture = tag.Pictures?.FirstOrDefault();
        
        // First, try embedded album art
        if (picture?.Data?.Data is { Length: > 0 } pictureData)
        {
            try
            {
                var (coverArtUri, lightSwatchId, darkSwatchId) =
                    await _imageProcessor.SaveCoverArtAndExtractColorsAsync(pictureData);

                metadata.CoverArtUri = coverArtUri;
                metadata.LightSwatchId = lightSwatchId;
                metadata.DarkSwatchId = darkSwatchId;
                return; // Successfully found embedded art, no need to search directories
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process embedded album art for '{FilePath}'.", metadata.FilePath);
                // Fall through to directory search
            }
        }

        // No embedded art found, search directory hierarchy for cover art files
        await ProcessCoverArtFromDirectoryAsync(metadata, baseFolderPath);
    }

    /// <summary>
    ///     Searches for cover art files in the directory hierarchy, starting from the song's
    ///     directory and walking up to the base folder path.
    /// </summary>
    private async Task ProcessCoverArtFromDirectoryAsync(SongFileMetadata metadata, string? baseFolderPath)
    {
        try
        {
            var coverArtPath = FindCoverArtInDirectoryHierarchy(metadata.FilePath, baseFolderPath);
            if (string.IsNullOrEmpty(coverArtPath)) return;

            // Read the image file and process it
            var imageBytes = await System.IO.File.ReadAllBytesAsync(coverArtPath);
            if (imageBytes.Length == 0) return;

            var (coverArtUri, lightSwatchId, darkSwatchId) =
                await _imageProcessor.SaveCoverArtAndExtractColorsAsync(imageBytes);

            metadata.CoverArtUri = coverArtUri;
            metadata.LightSwatchId = lightSwatchId;
            metadata.DarkSwatchId = darkSwatchId;

            _logger.LogDebug("Found cover art from directory for '{FilePath}': {CoverArtPath}",
                metadata.FilePath, coverArtPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to process directory cover art for '{FilePath}'.", metadata.FilePath);
        }
    }

    /// <summary>
    ///     Searches for a cover art file in the directory hierarchy, starting from the song's
    ///     directory and walking up to the base folder path. Looks for common cover art file
    ///     names (cover, folder, album, front) with supported image extensions.
    /// </summary>
    /// <param name="songFilePath">The path to the song file.</param>
    /// <param name="baseFolderPath">The root folder path to stop searching at.</param>
    /// <returns>The full path to the first matching cover art file, or null if none found.</returns>
    private string? FindCoverArtInDirectoryHierarchy(string songFilePath, string? baseFolderPath)
    {
        try
        {
            var currentDirectory = _fileSystem.GetDirectoryName(songFilePath);
            if (string.IsNullOrEmpty(currentDirectory)) return null;

            // Normalize the base folder path for comparison
            var normalizedBasePath = baseFolderPath?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            while (!string.IsNullOrEmpty(currentDirectory))
            {
                // Search for cover art in the current directory
                var coverArtPath = FindCoverArtInDirectory(currentDirectory);
                if (coverArtPath != null) return coverArtPath;

                // Check if we've reached or passed the base folder
                var normalizedCurrent = currentDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (!string.IsNullOrEmpty(normalizedBasePath) &&
                    normalizedCurrent.Equals(normalizedBasePath, StringComparison.OrdinalIgnoreCase))
                {
                    // We've searched the base folder, stop here
                    break;
                }

                // Move up to the parent directory
                var parentDirectory = _fileSystem.GetDirectoryName(currentDirectory);
                
                // Safety check: if parent equals current, we're at the root
                if (string.IsNullOrEmpty(parentDirectory) || 
                    parentDirectory.Equals(currentDirectory, StringComparison.OrdinalIgnoreCase))
                    break;

                currentDirectory = parentDirectory;
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error while searching for cover art in directory hierarchy for '{SongFilePath}'.",
                songFilePath);
            return null;
        }
    }

    /// <summary>
    ///     Searches for a cover art file in a specific directory by enumerating files
    ///     and matching against known cover art file names (case-insensitive).
    /// </summary>
    private string? FindCoverArtInDirectory(string directory)
    {
        try
        {
            // Enumerate files once and find matches - more efficient than checking each combination
            var files = _fileSystem.GetFiles(directory, "*.*");
            
            // First pass: find all matching cover art files
            string? bestMatch = null;
            var bestPriority = int.MaxValue;
            
            foreach (var filePath in files)
            {
                var extension = _fileSystem.GetExtension(filePath);
                if (!FileExtensions.ImageFileExtensions.Contains(extension))
                    continue;
                    
                var fileNameWithoutExt = _fileSystem.GetFileNameWithoutExtension(filePath);
                if (!FileExtensions.CoverArtFileNames.Contains(fileNameWithoutExt))
                    continue;
                
                // Determine priority based on position in the priority list
                var priority = GetCoverArtPriority(fileNameWithoutExt);
                if (priority < bestPriority)
                {
                    bestPriority = priority;
                    bestMatch = filePath;
                }
            }
            
            return bestMatch;
        }
        catch (Exception)
        {
            // If we can't enumerate the directory, return null
            return null;
        }
    }
    
    /// <summary>
    ///     Gets the priority of a cover art file name (lower is higher priority).
    /// </summary>
    private static int GetCoverArtPriority(string fileNameWithoutExt)
    {
        // Priority order: cover (0), folder (1), album (2), front (3)
        return fileNameWithoutExt.ToLowerInvariant() switch
        {
            "cover" => 0,
            "folder" => 1,
            "album" => 2,
            "front" => 3,
            _ => int.MaxValue
        };
    }

    /// <summary>
    ///     Gets the path to the LRC file, prioritizing a valid cache entry before
    ///     attempting to extract embedded lyrics from the audio file.
    /// </summary>
    private async Task<string?> GetLrcPathAsync(string audioFilePath, DateTime audioFileLastWriteTime, string? artist, string? title)
    {
        var cacheFileName = FileNameHelper.GenerateLrcCacheFileName(artist, title);
        var cachedLrcPath = _fileSystem.Combine(_pathConfig.LrcCachePath, cacheFileName);

        // Check for a valid cache entry. It's valid if it exists and is newer than the audio file.
        if (_fileSystem.FileExists(cachedLrcPath))
        {
            var cacheLastWriteTime = _fileSystem.GetLastWriteTimeUtc(cachedLrcPath);
            if (cacheLastWriteTime >= audioFileLastWriteTime) return cachedLrcPath;
        }

        try
        {
            // Use timeout for LRC extraction to prevent hangs
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10)); // 10-second timeout for LRC
            var lrcTask = Task.Run(() =>
            {
                using var tagFile = File.Create(new NonWritableFileAbstraction(audioFilePath));
                if (tagFile?.Tag is null) return null;
                return tagFile;
            }, cts.Token);

            var tagFile = await lrcTask;
            if (tagFile == null) return null;

            return await ExtractAndCacheEmbeddedLrcAsync(tagFile, cachedLrcPath);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("LRC extraction timed out for file: {AudioFilePath}", audioFilePath);
            return null;
        }
        catch (UnsupportedFormatException)
        {
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to extract or cache embedded LRC for '{AudioFilePath}'.", audioFilePath);
            return null;
        }
    }

    /// <summary>
    ///     Extracts embedded synchronized (SYLT) lyrics, converts them to LRC format, and saves them to the cache.
    /// </summary>
    private async Task<string?> ExtractAndCacheEmbeddedLrcAsync(File tagFile, string cachedLrcPath)
    {
        if (tagFile.GetTag(TagTypes.Id3v2, false) is not TagLib.Id3v2.Tag id3v2Tag) return null;

        // Find the best SYLT frame, preferring longer ones as they likely contain more lyrics.
        var bestSyltFrame = id3v2Tag.GetFrames<SynchronisedLyricsFrame>()
            .Where(f => f.Text.Length > 0)
            .OrderByDescending(f => f.Text.Length)
            .FirstOrDefault();

        if (bestSyltFrame is null) return null;

        // We only support the most common millisecond-based timestamps.
        if (bestSyltFrame.Format != TimestampFormat.AbsoluteMilliseconds)
        {
            _logger.LogDebug("Skipping embedded lyrics due to unsupported timestamp format: {TimestampFormat}",
                bestSyltFrame.Format);
            return null;
        }

        var lrcContentBuilder = new StringBuilder();
        var linesByTime = bestSyltFrame.Text
            .OrderBy(line => line.Time)
            .GroupBy(line => line.Time)
            .Select(group => new
            {
                Time = TimeSpan.FromMilliseconds(group.Key),
                Text = string.Join(" ", group.Select(l => l.Text?.Trim()).Where(t => !string.IsNullOrEmpty(t)))
            });

        foreach (var line in linesByTime)
        {
            if (string.IsNullOrWhiteSpace(line.Text)) continue;
            lrcContentBuilder.AppendLine($"[{line.Time:mm\\:ss\\.ff}]{line.Text}");
        }

        var lrcContent = lrcContentBuilder.ToString();
        if (string.IsNullOrWhiteSpace(lrcContent)) return null;

        await _fileSystem.WriteAllTextAsync(cachedLrcPath, lrcContent);
        return cachedLrcPath;
    }

    /// <summary>
    ///     Searches for an external .lrc file in the same directory as the audio file, matching by filename.
    /// </summary>
    private string? FindLrcFilePath(string audioFilePath)
    {
        try
        {
            var directory = _fileSystem.GetDirectoryName(audioFilePath);
            if (string.IsNullOrEmpty(directory)) return null;

            var audioFileNameWithoutExt = _fileSystem.GetFileNameWithoutExtension(audioFilePath);
            var lrcFiles = _fileSystem.GetFiles(directory, "*.lrc");

            return lrcFiles.FirstOrDefault(lrcPath =>
                _fileSystem.GetFileNameWithoutExtension(lrcPath)
                    .Equals(audioFileNameWithoutExt, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error while searching for external LRC file for '{AudioFilePath}'.",
                audioFilePath);
            return null;
        }
    }

    /// <summary>
    ///     Trims a string and returns null if the result is empty or whitespace, ensuring consistent null/empty handling.
    /// </summary>
    private string? SanitizeString(string? input)
    {
        return string.IsNullOrWhiteSpace(input) ? null : input.Trim();
    }
}
