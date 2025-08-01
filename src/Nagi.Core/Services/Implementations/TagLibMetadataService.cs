    using Nagi.Core.Helpers;
    using Nagi.Core.Models;
    using Nagi.Core.Services.Abstractions;
    using System;
    using System.Collections.Concurrent;
    using System.Diagnostics;
    using System.Linq;
    using System.Security.Cryptography;
    using System.Text;
    using System.Threading.Tasks;
    using TagLib;
    using TagLib.Id3v2;
    using Tag = TagLib.Tag;

    namespace Nagi.Core.Services.Implementations;

    /// <summary>
    /// Extracts music file metadata using the TagLib-Sharp library.
    /// This implementation supports external and embedded lyrics with performance-optimized caching for lyrics and album art.
    /// </summary>
    public class TagLibMetadataService : IMetadataService {
        private const string UnknownArtistName = "Unknown Artist";
        private const string UnknownAlbumName = "Unknown Album";

        private readonly IFileSystemService _fileSystem;
        private readonly IImageProcessor _imageProcessor;
        private readonly IPathConfiguration _pathConfig;

        // A thread-safe dictionary of semaphores to prevent race conditions when processing album art for the same album concurrently.
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _albumArtWriteSemaphores = new();

        public TagLibMetadataService(IImageProcessor imageProcessor, IFileSystemService fileSystem, IPathConfiguration pathConfig) {
            _imageProcessor = imageProcessor ?? throw new ArgumentNullException(nameof(imageProcessor));
            _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
            _pathConfig = pathConfig ?? throw new ArgumentNullException(nameof(pathConfig));
        }

        /// <inheritdoc />
        public async Task<SongFileMetadata> ExtractMetadataAsync(string filePath) {
            var metadata = new SongFileMetadata { FilePath = filePath };

            try {
                var fileInfo = _fileSystem.GetFileInfo(filePath);
                metadata.FileCreatedDate = fileInfo.CreationTimeUtc;
                metadata.FileModifiedDate = fileInfo.LastWriteTimeUtc;
                metadata.Title = _fileSystem.GetFileNameWithoutExtension(filePath);

                // Prioritize lyrics extraction. First, check for cached or embedded synchronized lyrics.
                metadata.LrcFilePath = await GetLrcPathAsync(filePath, fileInfo.LastWriteTimeUtc);

                // As a fallback, look for an external .lrc file if no embedded lyrics were found.
                if (string.IsNullOrWhiteSpace(metadata.LrcFilePath)) {
                    metadata.LrcFilePath = FindLrcFilePath(filePath);
                }

                // Use a read-only file abstraction for safety and to prevent file locks.
                using var tagFile = TagLib.File.Create(new NonWritableFileAbstraction(filePath));

                if (tagFile?.Tag is null || tagFile.Properties is null) {
                    throw new CorruptFileException("TagLib could not read the file's tag or property structures.");
                }

                PopulateMetadataFromTag(metadata, tagFile.Tag, tagFile.Properties);
                await ProcessAlbumArtAsync(metadata, tagFile.Tag);
            }
            catch (CorruptFileException ex) {
                Debug.WriteLine($"[MetadataExtractor] Corrupt file detected. Path: '{filePath}', Reason: {ex.Message}");
                metadata.ExtractionFailed = true;
                metadata.ErrorMessage = "CorruptFile";
            }
            catch (UnsupportedFormatException) {
                Debug.WriteLine($"[MetadataExtractor] Unsupported file format. Path: '{filePath}'");
                metadata.ExtractionFailed = true;
                metadata.ErrorMessage = "UnsupportedFormat";
            }
            catch (Exception ex) {
                Debug.WriteLine($"[MetadataExtractor] An unexpected error occurred during metadata extraction. Path: '{filePath}', Error: {ex.GetType().Name} - {ex.Message}");
                metadata.ExtractionFailed = true;
                metadata.ErrorMessage = ex.GetType().Name;
            }

            return metadata;
        }

        /// <summary>
        /// Populates the metadata object from the file's tags and properties, providing sane defaults for missing values.
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
        /// Extracts album art from the tag, saves it to a file, and extracts color information.
        /// Uses a semaphore to ensure that art for the same album is not processed multiple times concurrently.
        /// </summary>
        private async Task ProcessAlbumArtAsync(SongFileMetadata metadata, Tag tag) {
            var picture = tag.Pictures?.FirstOrDefault();
            if (picture?.Data?.Data is not { Length: > 0 } pictureData) return;

            // A key based on the album and artist uniquely identifies the album art, preventing redundant processing.
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
        /// Gets the path to the LRC file, prioritizing a valid cache entry before
        /// attempting to extract embedded lyrics from the audio file.
        /// </summary>
        /// <param name="audioFilePath">The full path to the audio file.</param>
        /// <param name="audioFileLastWriteTime">The last modification time of the audio file, used for cache validation.</param>
        /// <returns>The path to the cached LRC file, or null if not found or extracted.</returns>
        private async Task<string?> GetLrcPathAsync(string audioFilePath, DateTime audioFileLastWriteTime) {
            // A cryptographic hash of the file path creates a deterministic and unique filename for the cache entry.
            string cacheKey;
            using (var sha256 = SHA256.Create()) {
                var hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(audioFilePath));
                cacheKey = Convert.ToBase64String(hashBytes).Replace('/', '_').Replace('+', '-');
            }

            var cachedLrcPath = _fileSystem.Combine(_pathConfig.LrcCachePath, $"{cacheKey}.lrc");

            // Validate the cache by comparing its modification time with the source audio file's modification time.
            // This avoids re-extracting lyrics from an unchanged file.
            if (_fileSystem.FileExists(cachedLrcPath)) {
                var cacheLastWriteTime = _fileSystem.GetLastWriteTimeUtc(cachedLrcPath);
                if (cacheLastWriteTime >= audioFileLastWriteTime) {
                    return cachedLrcPath;
                }
            }

            try {
                using var tagFile = TagLib.File.Create(new NonWritableFileAbstraction(audioFilePath));
                if (tagFile?.Tag is null) return null;

                return await ExtractAndCacheEmbeddedLrcAsync(tagFile, cachedLrcPath);
            }
            catch (UnsupportedFormatException) {
                // This format is not supported by TagLib for tag reading, so we cannot extract embedded lyrics.
                return null;
            }
            catch (Exception ex) {
                Debug.WriteLine($"[MetadataExtractor] Failed to extract or cache embedded LRC for '{audioFilePath}'. Error: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Extracts embedded synchronized (SYLT) lyrics, converts them to LRC format, and saves them to the cache.
        /// </summary>
        /// <param name="tagFile">The TagLib file object to read from.</param>
        /// <param name="cachedLrcPath">The destination path for the cached LRC file.</param>
        /// <returns>The path to the cached LRC file if successful; otherwise, null.</returns>
        private async Task<string?> ExtractAndCacheEmbeddedLrcAsync(TagLib.File tagFile, string cachedLrcPath) {
            // Explicitly request the ID3v2 tag, as SYLT frames are specific to this format.
            // The 'false' parameter prevents creating a new tag if one doesn't exist.
            if (tagFile.GetTag(TagTypes.Id3v2, false) is not TagLib.Id3v2.Tag id3v2Tag) {
                return null;
            }

            // Find the best available synchronized lyrics frame, preferring the one with the most content.
            var bestSyltFrame = id3v2Tag.GetFrames<SynchronisedLyricsFrame>()
                .Where(f => f.Text.Length > 0)
                .OrderByDescending(f => f.Text.Length)
                .FirstOrDefault();

            if (bestSyltFrame is null) {
                return null;
            }

            // The application's LRC parser only supports millisecond-based timestamps.
            if (bestSyltFrame.Format != TimestampFormat.AbsoluteMilliseconds) {
                Debug.WriteLine($"[LRC Extractor] Skipping embedded lyrics due to unsupported timestamp format: {bestSyltFrame.Format}");
                return null;
            }

            var lrcContentBuilder = new StringBuilder();

            // Group lyrics by timestamp to merge any lines that share the exact same time, a common occurrence in some files.
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
            if (string.IsNullOrWhiteSpace(lrcContent)) {
                Debug.WriteLine("[LRC Extractor] Processed SYLT frame, but the resulting LRC content was empty.");
                return null;
            }

            await _fileSystem.WriteAllTextAsync(cachedLrcPath, lrcContent);
            Debug.WriteLine($"[LRC Extractor] LRC content successfully cached at '{cachedLrcPath}'.");

            return cachedLrcPath;
        }

        /// <summary>
        /// Searches for an external .lrc file in the same directory as the audio file, matching by filename.
        /// </summary>
        /// <param name="audioFilePath">The full path to the audio file.</param>
        /// <returns>The path to the matching .lrc file, or null if not found.</returns>
        private string? FindLrcFilePath(string audioFilePath) {
            try {
                var directory = _fileSystem.GetDirectoryName(audioFilePath);
                if (string.IsNullOrEmpty(directory)) return null;

                var audioFileNameWithoutExt = _fileSystem.GetFileNameWithoutExtension(audioFilePath);
                var lrcFiles = _fileSystem.GetFiles(directory, "*.lrc");

                // Find an LRC file with a name that matches the audio file's name, ignoring case for compatibility.
                return lrcFiles.FirstOrDefault(lrcPath =>
                    _fileSystem.GetFileNameWithoutExtension(lrcPath)
                        .Equals(audioFileNameWithoutExt, StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception ex) {
                Debug.WriteLine($"[MetadataExtractor] Error while searching for LRC file for '{audioFilePath}'. Error: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Trims a string and returns null if the result is empty or whitespace, ensuring consistent null/empty handling.
        /// </summary>
        /// <param name="input">The string to sanitize.</param>
        /// <returns>The trimmed string or null.</returns>
        private string? SanitizeString(string? input) {
            return string.IsNullOrWhiteSpace(input) ? null : input.Trim();
        }
    }