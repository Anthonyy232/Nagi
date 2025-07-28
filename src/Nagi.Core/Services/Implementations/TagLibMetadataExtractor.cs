using System.Collections.Concurrent;
using System.Diagnostics;
using Nagi.Core.Helpers;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using TagLib;

namespace Nagi.Core.Services.Implementations;

/// <summary>
/// An implementation of <see cref="IMetadataExtractor"/> that uses the TagLib-Sharp library
/// to extract metadata from music files.
/// </summary>
public class TagLibMetadataExtractor : IMetadataExtractor {
    private const string UnknownArtistName = "Unknown Artist";
    private const string UnknownAlbumName = "Unknown Album";

    private readonly IFileSystemService _fileSystem;
    private readonly IImageProcessor _imageProcessor;

    // A dictionary of semaphores to prevent race conditions when writing album art files.
    // The key is a composite of album artist and album name.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _albumArtWriteSemaphores = new();

    public TagLibMetadataExtractor(IImageProcessor imageProcessor, IFileSystemService fileSystem) {
        _imageProcessor = imageProcessor ?? throw new ArgumentNullException(nameof(imageProcessor));
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
    }

    /// <inheritdoc />
    public async Task<SongFileMetadata> ExtractMetadataAsync(string filePath) {
        var metadata = new SongFileMetadata { FilePath = filePath };

        try {
            // Populate basic file system information and set a fallback title.
            var fileInfo = _fileSystem.GetFileInfo(filePath);
            metadata.FileCreatedDate = fileInfo.CreationTimeUtc;
            metadata.FileModifiedDate = fileInfo.LastWriteTimeUtc;
            metadata.Title = _fileSystem.GetFileNameWithoutExtension(filePath);

            // Find an associated LRC file path, if one exists.
            metadata.LrcFilePath = FindLrcFilePath(filePath);

            // Use a read-only abstraction for safety and performance.
            using var tagFile = TagLib.File.Create(new NonWritableFileAbstraction(filePath));

            // If TagLib can't read the essential parts of the file, we cannot proceed.
            if (tagFile?.Tag is null || tagFile.Properties is null) {
                throw new CorruptFileException("TagLib could not read the file's tag or property structures.");
            }

            PopulateMetadataFromTag(metadata, tagFile.Tag, tagFile.Properties);
            await ProcessAlbumArtAsync(metadata, tagFile.Tag);
        }
        catch (CorruptFileException ex) {
            Trace.TraceWarning($"[MetadataExtractor] Corrupt file detected. Path: '{filePath}', Reason: {ex.Message}");
            metadata.ExtractionFailed = true;
            metadata.ErrorMessage = "CorruptFile";
        }
        catch (UnsupportedFormatException) {
            Trace.TraceWarning($"[MetadataExtractor] Unsupported file format. Path: '{filePath}'");
            metadata.ExtractionFailed = true;
            metadata.ErrorMessage = "UnsupportedFormat";
        }
        catch (Exception ex) {
            Trace.TraceWarning(
                $"[MetadataExtractor] An unexpected error occurred during metadata extraction. Path: '{filePath}', Error: {ex.GetType().Name} - {ex.Message}");
            metadata.ExtractionFailed = true;
            metadata.ErrorMessage = ex.GetType().Name;
        }

        return metadata;
    }

    /// <summary>
    /// Populates the metadata object from the TagLib tag and properties.
    /// </summary>
    /// <param name="metadata">The metadata object to populate.</param>
    /// <param name="tag">The extracted <see cref="Tag"/> from the file.</param>
    /// <param name="properties">The extracted <see cref="Properties"/> from the file.</param>
    private void PopulateMetadataFromTag(SongFileMetadata metadata, Tag tag, Properties properties) {
        // Extract primary metadata, providing fallbacks for essential fields.
        var artist = SanitizeString(tag.Performers?.FirstOrDefault()) ?? UnknownArtistName;
        var albumArtist = SanitizeString(tag.AlbumArtists?.FirstOrDefault()) ?? artist;
        var album = SanitizeString(tag.Album) ?? UnknownAlbumName;

        // If the title tag is empty, the fallback to filename from the calling method is used.
        metadata.Title = SanitizeString(tag.Title) ?? metadata.Title;
        metadata.Artist = artist;
        metadata.Album = album;
        metadata.AlbumArtist = albumArtist;
        metadata.Duration = properties.Duration;

        // For numeric tags, a value of 0 is often used to indicate "not set".
        metadata.Year = tag.Year > 0 ? (int)tag.Year : null;
        metadata.TrackNumber = tag.Track > 0 ? (int)tag.Track : null;
        metadata.TrackCount = tag.TrackCount > 0 ? (int)tag.TrackCount : null;
        metadata.DiscNumber = tag.Disc > 0 ? (int)tag.Disc : null;
        metadata.DiscCount = tag.DiscCount > 0 ? (int)tag.DiscCount : null;
        metadata.Bpm = tag.BeatsPerMinute > 0 ? tag.BeatsPerMinute : null;

        // Extract audio stream properties.
        metadata.SampleRate = properties.AudioSampleRate > 0 ? properties.AudioSampleRate : null;
        metadata.Bitrate = properties.AudioBitrate > 0 ? properties.AudioBitrate : null;
        metadata.Channels = properties.AudioChannels > 0 ? properties.AudioChannels : null;

        // Extract other nullable string-based metadata.
        metadata.Lyrics = SanitizeString(tag.Lyrics);
        metadata.Composer = SanitizeString(tag.Composers?.FirstOrDefault());
        metadata.Grouping = SanitizeString(tag.Grouping);
        metadata.Copyright = SanitizeString(tag.Copyright);
        metadata.Comment = SanitizeString(tag.Comment);
        metadata.Conductor = SanitizeString(tag.Conductor);
        metadata.MusicBrainzTrackId = SanitizeString(tag.MusicBrainzTrackId);
        metadata.MusicBrainzReleaseId = SanitizeString(tag.MusicBrainzReleaseId);

        // Extract and sanitize genres.
        metadata.Genres = tag.Genres
            ?.Select(SanitizeString)
            .Where(g => g is not null)
            .Select(g => g!) // The Where clause ensures g is not null here.
            .ToList() ?? [];
    }

    /// <summary>
    /// Extracts album art from the tag, saves it to storage, and extracts color information.
    /// </summary>
    /// <param name="metadata">The metadata object to update with art and color info.</param>
    /// <param name="tag">The <see cref="Tag"/> containing the picture data.</param>
    private async Task ProcessAlbumArtAsync(SongFileMetadata metadata, Tag tag) {
        var picture = tag.Pictures?.FirstOrDefault();

        // Proceed only if there is valid picture data.
        if (picture?.Data?.Data is not { Length: > 0 } pictureData) {
            return;
        }

        // Use a semaphore to prevent race conditions when multiple files from the same album
        // are processed concurrently, which could lead to simultaneous writes of the same album art file.
        var artKey = $"{metadata.AlbumArtist}_{metadata.Album}";
        var semaphore = _albumArtWriteSemaphores.GetOrAdd(artKey, _ => new SemaphoreSlim(1, 1));

        await semaphore.WaitAsync();
        try {
            (metadata.CoverArtUri, metadata.LightSwatchId, metadata.DarkSwatchId) =
                await _imageProcessor.SaveCoverArtAndExtractColorsAsync(pictureData, metadata.Album!, metadata.AlbumArtist!);
        }
        finally {
            semaphore.Release();
        }
    }

    /// <summary>
    /// Finds an associated LRC lyrics file for a given audio file in the same directory.
    /// The match is performed case-insensitively on the file name without its extension.
    /// </summary>
    /// <param name="audioFilePath">The full path to the audio file.</param>
    /// <returns>The full path to the matching .lrc file, or null if not found.</returns>
    private string? FindLrcFilePath(string audioFilePath) {
        try {
            var directory = _fileSystem.GetDirectoryName(audioFilePath);
            if (string.IsNullOrEmpty(directory)) {
                // This can happen if the path is just a filename without a directory.
                return null;
            }

            var audioFileNameWithoutExt = _fileSystem.GetFileNameWithoutExtension(audioFilePath);

            // Search for all .lrc files in the directory. This is generally efficient as the
            // pattern is filtered at the OS level.
            var lrcFiles = _fileSystem.GetFiles(directory, "*.lrc");

            // Find the first .lrc file that matches the audio file's name, ignoring case.
            return lrcFiles.FirstOrDefault(lrcPath =>
                _fileSystem.GetFileNameWithoutExtension(lrcPath)
                    .Equals(audioFileNameWithoutExt, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) {
            // Log the error but don't let it stop the entire metadata extraction process.
            Trace.TraceWarning($"[MetadataExtractor] Error while searching for LRC file for '{audioFilePath}'. Error: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Cleans a string from metadata tags.
    /// </summary>
    /// <param name="input">The string to sanitize.</param>
    /// <returns>A trimmed string, or null if the input is null, empty, or only whitespace.</returns>
    private string? SanitizeString(string? input) {
        return string.IsNullOrWhiteSpace(input) ? null : input.Trim();
    }
}