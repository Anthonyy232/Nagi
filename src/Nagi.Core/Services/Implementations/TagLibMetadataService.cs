using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
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
public class TagLibMetadataService : IMetadataService {
    private const string UnknownArtistName = "Unknown Artist";
    private const string UnknownAlbumName = "Unknown Album";

    private readonly
        ConcurrentDictionary<string, Lazy<Task<(string? CoverArtUri, string? LightSwatchId, string? DarkSwatchId)>>>
        _albumArtProcessingTasks = new();

    private readonly IFileSystemService _fileSystem;
    private readonly IImageProcessor _imageProcessor;
    private readonly IPathConfiguration _pathConfig;
    private readonly ILogger<TagLibMetadataService> _logger;

    public TagLibMetadataService(IImageProcessor imageProcessor, IFileSystemService fileSystem,
        IPathConfiguration pathConfig, ILogger<TagLibMetadataService> logger) {
        _imageProcessor = imageProcessor ?? throw new ArgumentNullException(nameof(imageProcessor));
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _pathConfig = pathConfig ?? throw new ArgumentNullException(nameof(pathConfig));
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<SongFileMetadata> ExtractMetadataAsync(string filePath) {
        var metadata = new SongFileMetadata { FilePath = filePath };

        try {
            var fileInfo = _fileSystem.GetFileInfo(filePath);
            metadata.FileCreatedDate = fileInfo.CreationTimeUtc;
            metadata.FileModifiedDate = fileInfo.LastWriteTimeUtc;
            metadata.Title = _fileSystem.GetFileNameWithoutExtension(filePath);

            // Get cached or extract new synchronized lyrics.
            metadata.LrcFilePath = await GetLrcPathAsync(filePath, fileInfo.LastWriteTimeUtc);

            // As a fallback, look for an external .lrc file if no embedded lyrics were found.
            if (string.IsNullOrWhiteSpace(metadata.LrcFilePath)) metadata.LrcFilePath = FindLrcFilePath(filePath);

            // Use a read-only file abstraction for safety and to prevent file locks.
            using var tagFile = File.Create(new NonWritableFileAbstraction(filePath));

            if (tagFile?.Tag is null || tagFile.Properties is null)
                throw new CorruptFileException("TagLib could not read the file's tag or property structures.");

            PopulateMetadataFromTag(metadata, tagFile.Tag, tagFile.Properties);
            await ProcessAlbumArtAsync(metadata, tagFile.Tag);
        }
        catch (CorruptFileException ex) {
            _logger.LogWarning(ex, "Corrupt file detected while extracting metadata from '{FilePath}'.", filePath);
            metadata.ExtractionFailed = true;
            metadata.ErrorMessage = "CorruptFile";
        }
        catch (UnsupportedFormatException) {
            _logger.LogWarning("Unsupported file format for metadata extraction: '{FilePath}'.", filePath);
            metadata.ExtractionFailed = true;
            metadata.ErrorMessage = "UnsupportedFormat";
        }
        catch (Exception ex) {
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
    private void PopulateMetadataFromTag(SongFileMetadata metadata, Tag tag, Properties properties) {
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
    ///     Extracts album art from the tag and processes it. This method ensures the expensive image processing
    ///     for a given album occurs exactly once, even under high concurrency. All concurrent callers for the
    ///     same album will await the result of the single, shared operation.
    /// </summary>
    private async Task ProcessAlbumArtAsync(SongFileMetadata metadata, Tag tag) {
        var picture = tag.Pictures?.FirstOrDefault();
        if (picture?.Data?.Data is not { Length: > 0 } pictureData) return;

        var artKey = $"{metadata.AlbumArtist}_{metadata.Album}";

        // Atomically get or create the lazy-initialized task for this album art.
        // The factory function inside new Lazy<T> will only ever be executed ONCE per artKey.
        var lazyTask = _albumArtProcessingTasks.GetOrAdd(artKey, _ =>
            new Lazy<Task<(string?, string?, string?)>>(() =>
                _imageProcessor.SaveCoverArtAndExtractColorsAsync(pictureData, metadata.Album!, metadata.AlbumArtist!)
            )
        );

        try {
            // All threads for the same album will await the SAME task instance here.
            // If the task has already completed, the result is returned instantly.
            // If it's in progress, they wait for it to finish.
            // If it hasn't started, awaiting .Value triggers the factory method above.
            var (coverArtUri, lightSwatchId, darkSwatchId) = await lazyTask.Value;

            metadata.CoverArtUri = coverArtUri;
            metadata.LightSwatchId = lightSwatchId;
            metadata.DarkSwatchId = darkSwatchId;
        }
        catch (Exception ex) {
            // CRITICAL: If image processing fails, the Lazy<Task> caches the exception and
            // will re-throw it on every subsequent access, "poisoning" the cache for this key.
            // We must remove the failed entry so that a subsequent request can retry the operation.
            _logger.LogError(ex, "Failed to process album art for key '{ArtKey}'. Removing from cache to allow retry.",
                artKey);
            _albumArtProcessingTasks.TryRemove(
                new KeyValuePair<string,
                    Lazy<Task<(string? CoverArtUri, string? LightSwatchId, string? DarkSwatchId)>>>(artKey, lazyTask));
            // We don't re-throw, as failing to get album art shouldn't fail the entire metadata extraction.
        }
    }

    /// <summary>
    ///     Gets the path to the LRC file, prioritizing a valid cache entry before
    ///     attempting to extract embedded lyrics from the audio file.
    /// </summary>
    private async Task<string?> GetLrcPathAsync(string audioFilePath, DateTime audioFileLastWriteTime) {
        string cacheKey;
        using (var sha256 = SHA256.Create()) {
            var hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(audioFilePath));
            cacheKey = Convert.ToBase64String(hashBytes).Replace('/', '_').Replace('+', '-');
        }

        var cachedLrcPath = _fileSystem.Combine(_pathConfig.LrcCachePath, $"{cacheKey}.lrc");

        // Check for a valid cache entry. It's valid if it exists and is newer than the audio file.
        if (_fileSystem.FileExists(cachedLrcPath)) {
            var cacheLastWriteTime = _fileSystem.GetLastWriteTimeUtc(cachedLrcPath);
            if (cacheLastWriteTime >= audioFileLastWriteTime) return cachedLrcPath;
        }

        try {
            using var tagFile = File.Create(new NonWritableFileAbstraction(audioFilePath));
            if (tagFile?.Tag is null) return null;

            return await ExtractAndCacheEmbeddedLrcAsync(tagFile, cachedLrcPath);
        }
        catch (UnsupportedFormatException) {
            return null;
        }
        catch (Exception ex) {
            _logger.LogWarning(ex, "Failed to extract or cache embedded LRC for '{AudioFilePath}'.", audioFilePath);
            return null;
        }
    }

    /// <summary>
    ///     Extracts embedded synchronized (SYLT) lyrics, converts them to LRC format, and saves them to the cache.
    /// </summary>
    private async Task<string?> ExtractAndCacheEmbeddedLrcAsync(File tagFile, string cachedLrcPath) {
        if (tagFile.GetTag(TagTypes.Id3v2, false) is not TagLib.Id3v2.Tag id3v2Tag) return null;

        // Find the best SYLT frame, preferring longer ones as they likely contain more lyrics.
        var bestSyltFrame = id3v2Tag.GetFrames<SynchronisedLyricsFrame>()
            .Where(f => f.Text.Length > 0)
            .OrderByDescending(f => f.Text.Length)
            .FirstOrDefault();

        if (bestSyltFrame is null) return null;

        // We only support the most common millisecond-based timestamps.
        if (bestSyltFrame.Format != TimestampFormat.AbsoluteMilliseconds) {
            _logger.LogDebug("Skipping embedded lyrics due to unsupported timestamp format: {TimestampFormat}",
                bestSyltFrame.Format);
            return null;
        }

        var lrcContentBuilder = new StringBuilder();
        var linesByTime = bestSyltFrame.Text
            .OrderBy(line => line.Time)
            .GroupBy(line => line.Time)
            .Select(group => new {
                Time = TimeSpan.FromMilliseconds(group.Key),
                Text = string.Join(" ", group.Select(l => l.Text?.Trim()).Where(t => !string.IsNullOrEmpty(t)))
            });

        foreach (var line in linesByTime) {
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
    private string? FindLrcFilePath(string audioFilePath) {
        try {
            var directory = _fileSystem.GetDirectoryName(audioFilePath);
            if (string.IsNullOrEmpty(directory)) return null;

            var audioFileNameWithoutExt = _fileSystem.GetFileNameWithoutExtension(audioFilePath);
            var lrcFiles = _fileSystem.GetFiles(directory, "*.lrc");

            return lrcFiles.FirstOrDefault(lrcPath =>
                _fileSystem.GetFileNameWithoutExtension(lrcPath)
                    .Equals(audioFileNameWithoutExt, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) {
            _logger.LogWarning(ex, "Error while searching for external LRC file for '{AudioFilePath}'.",
                audioFilePath);
            return null;
        }
    }

    /// <summary>
    ///     Trims a string and returns null if the result is empty or whitespace, ensuring consistent null/empty handling.
    /// </summary>
    private string? SanitizeString(string? input) {
        return string.IsNullOrWhiteSpace(input) ? null : input.Trim();
    }
}