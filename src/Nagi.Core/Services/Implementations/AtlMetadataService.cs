using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ATL;
using Microsoft.Extensions.Logging;
using Nagi.Core.Constants;
using Nagi.Core.Helpers;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;

namespace Nagi.Core.Services.Implementations;

/// <summary>
///     Extracts music file metadata using the ATL.NET library.
/// </summary>
public class AtlMetadataService : IMetadataService
{
    private const string UnknownArtistName = "Unknown Artist";
    private const string UnknownAlbumName = "Unknown Album";

    private readonly IFileSystemService _fileSystem;
    private readonly IImageProcessor _imageProcessor;
    private readonly ILogger<AtlMetadataService> _logger;
    private readonly IPathConfiguration _pathConfig;

    public AtlMetadataService(IImageProcessor imageProcessor, IFileSystemService fileSystem,
        IPathConfiguration pathConfig, ILogger<AtlMetadataService> logger)
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

            // Use a timeout wrapper for ATL operations to prevent indefinite hangs
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var extractionTask = Task.Run(() => new Track(filePath), cts.Token);

            var track = await extractionTask;

            // Check if the file is valid - ATL is lenient so we need multiple checks
            // Check 1: AudioFormat.Readable flag
            // Check 2: Format name is "Unknown" (indicates ATL couldn't identify the format)
            // Check 3: Duration is 0 and no audio data detected
            var isUnknownFormat = track.AudioFormat.Name?.Equals("Unknown", StringComparison.OrdinalIgnoreCase) == true ||
                                  track.AudioFormat.ID == -1;
            
            if (!track.AudioFormat.Readable || isUnknownFormat)
            {
                metadata.ExtractionFailed = true;
                metadata.ErrorMessage = isUnknownFormat ? "UnsupportedFormat" : "CorruptFile";
                return metadata;
            }

            PopulateMetadataFromTrack(metadata, track);

            // Get cached or extract new synchronized lyrics (after parsing track so we have artist/title).
            using var lrcCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                metadata.LrcFilePath = await GetLrcPathAsync(filePath, fileInfo.LastWriteTimeUtc, metadata.Artist, metadata.Title, track)
                    .WaitAsync(lrcCts.Token);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("LRC extraction timed out for file: {FilePath}", filePath);
            }

            // As a fallback, look for an external .lrc file if no embedded lyrics were found.
            if (string.IsNullOrWhiteSpace(metadata.LrcFilePath)) metadata.LrcFilePath = FindLrcFilePath(filePath);

            // Process album art with timeout protection
            using var albumArtCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try
            {
                await ProcessAlbumArtAsync(metadata, track, baseFolderPath).WaitAsync(albumArtCts.Token);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Album art extraction timed out for file: {FilePath}", filePath);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Metadata extraction timed out for file: {FilePath}", filePath);
            metadata.ExtractionFailed = true;
            metadata.ErrorMessage = "ExtractionTimeout";
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "File access error during metadata extraction for '{FilePath}'.", filePath);
            metadata.ExtractionFailed = true;
            metadata.ErrorMessage = "FileAccessError";
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "Access denied during metadata extraction for '{FilePath}'.", filePath);
            metadata.ExtractionFailed = true;
            metadata.ErrorMessage = "AccessDenied";
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
    ///     Populates the metadata object from the ATL track, providing sane defaults for missing values.
    /// </summary>
    private void PopulateMetadataFromTrack(SongFileMetadata metadata, Track track)
    {
        var artist = SanitizeString(track.Artist) ?? UnknownArtistName;
        var albumArtist = SanitizeString(track.AlbumArtist) ?? artist;
        var album = SanitizeString(track.Album) ?? UnknownAlbumName;

        metadata.Title = SanitizeString(track.Title) ?? metadata.Title;
        metadata.Artist = artist;
        metadata.Album = album;
        metadata.AlbumArtist = albumArtist;
        metadata.Duration = TimeSpan.FromSeconds(track.Duration);
        metadata.Year = track.Year > 0 ? track.Year : null;
        metadata.TrackNumber = track.TrackNumber > 0 ? track.TrackNumber : null;
        metadata.TrackCount = track.TrackTotal > 0 ? track.TrackTotal : null;
        metadata.DiscNumber = track.DiscNumber > 0 ? track.DiscNumber : null;
        metadata.DiscCount = track.DiscTotal > 0 ? track.DiscTotal : null;
        metadata.Bpm = track.BPM > 0 ? track.BPM : null;
        metadata.SampleRate = track.SampleRate > 0 ? (int)track.SampleRate : null;
        metadata.Bitrate = track.Bitrate > 0 ? track.Bitrate : null;
        metadata.Channels = track.ChannelsArrangement?.NbChannels > 0 ? track.ChannelsArrangement.NbChannels : null;

        // Get unsynchronized lyrics from the first lyrics entry if available
        var lyricsInfo = track.Lyrics?.FirstOrDefault();
        if (lyricsInfo != null)
        {
            metadata.Lyrics = SanitizeString(lyricsInfo.UnsynchronizedLyrics);
        }

        metadata.Composer = SanitizeString(track.Composer);
        metadata.Copyright = SanitizeString(track.Copyright);
        metadata.Comment = SanitizeString(track.Comment);
        metadata.Conductor = SanitizeString(track.Conductor);

        // ATL uses AdditionalFields for MusicBrainz IDs
        if (track.AdditionalFields.TryGetValue("MUSICBRAINZ_TRACKID", out var mbTrackId))
            metadata.MusicBrainzTrackId = SanitizeString(mbTrackId);
        if (track.AdditionalFields.TryGetValue("MUSICBRAINZ_RELEASEID", out var mbReleaseId) ||
            track.AdditionalFields.TryGetValue("MUSICBRAINZ_ALBUMID", out mbReleaseId))
            metadata.MusicBrainzReleaseId = SanitizeString(mbReleaseId);

        // Parse genre (ATL returns a single string, may need to split)
        var genreString = SanitizeString(track.Genre);
        if (!string.IsNullOrEmpty(genreString))
        {
            metadata.Genres = genreString
                .Split(new[] { ';', '/', '\\' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(g => g.Trim())
                .Where(g => !string.IsNullOrEmpty(g))
                .ToList();
        }
        else
        {
            metadata.Genres = [];
        }

        // ATL doesn't have a direct Grouping property, check additional fields
        if (track.AdditionalFields.TryGetValue("GRP1", out var grouping) ||
            track.AdditionalFields.TryGetValue("CONTENTGROUP", out grouping) ||
            track.AdditionalFields.TryGetValue("GROUPING", out grouping) ||
            track.AdditionalFields.TryGetValue("TIT1", out grouping))
            metadata.Grouping = SanitizeString(grouping);

        // Extract ReplayGain tags (case-insensitive lookup)
        var gainKey = track.AdditionalFields.Keys
            .FirstOrDefault(k => k.Equals("REPLAYGAIN_TRACK_GAIN", StringComparison.OrdinalIgnoreCase));
        if (gainKey != null && track.AdditionalFields.TryGetValue(gainKey, out var gainStr))
            metadata.ReplayGainTrackGain = ParseReplayGainValue(gainStr);
        
        var peakKey = track.AdditionalFields.Keys
            .FirstOrDefault(k => k.Equals("REPLAYGAIN_TRACK_PEAK", StringComparison.OrdinalIgnoreCase));
        if (peakKey != null && track.AdditionalFields.TryGetValue(peakKey, out var peakStr))
            metadata.ReplayGainTrackPeak = ParseReplayGainValue(peakStr);
    }

    /// <summary>
    ///     Extracts album art from the track and saves it to the cache. Each song gets its own
    ///     cached cover art file, keyed by the song's file path. If no embedded art is found,
    ///     falls back to searching directory hierarchy for common cover art files.
    /// </summary>
    private async Task ProcessAlbumArtAsync(SongFileMetadata metadata, Track track, string? baseFolderPath)
    {
        var picture = track.EmbeddedPictures?.FirstOrDefault();

        // First, try embedded album art
        if (picture?.PictureData is { Length: > 0 } pictureData)
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
            var imageBytes = await _fileSystem.ReadAllBytesAsync(coverArtPath);
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
        catch (Exception ex)
        {
            // If we can't enumerate the directory, log and return null
            _logger.LogDebug(ex, "Failed to enumerate cover art in directory '{Directory}'.", directory);
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
    private async Task<string?> GetLrcPathAsync(string audioFilePath, DateTime audioFileLastWriteTime, string? artist, string? title, Track track)
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
            return await ExtractAndCacheEmbeddedLrcAsync(track, cachedLrcPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to extract or cache embedded LRC for '{AudioFilePath}'.", audioFilePath);
            return null;
        }
    }

    /// <summary>
    ///     Extracts embedded synchronized lyrics from ATL track, converts them to LRC format, and saves them to the cache.
    /// </summary>
    private async Task<string?> ExtractAndCacheEmbeddedLrcAsync(Track track, string cachedLrcPath)
    {
        var lyricsInfo = track.Lyrics?.FirstOrDefault();
        if (lyricsInfo == null) return null;

        var syncLyrics = lyricsInfo.SynchronizedLyrics;
        if (syncLyrics == null || syncLyrics.Count == 0) return null;

        var lrcContentBuilder = new StringBuilder();

        foreach (var phrase in syncLyrics.OrderBy(p => p.TimestampStart))
        {
            var text = phrase.Text?.Trim();
            if (string.IsNullOrWhiteSpace(text)) continue;

            var time = TimeSpan.FromMilliseconds(phrase.TimestampStart);
            lrcContentBuilder.AppendLine($"[{time:mm\\:ss\\.fff}]{text}");
        }

        var lrcContent = lrcContentBuilder.ToString();
        if (string.IsNullOrWhiteSpace(lrcContent)) return null;

        // Ensure the LRC cache directory exists before writing
        var cacheDirectory = _fileSystem.GetDirectoryName(cachedLrcPath);
        if (!string.IsNullOrEmpty(cacheDirectory) && !_fileSystem.DirectoryExists(cacheDirectory))
        {
            _fileSystem.CreateDirectory(cacheDirectory);
        }

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

    /// <summary>
    ///     Parses a ReplayGain value string (e.g., "-6.54 dB" or "0.98") to a nullable double.
    ///     Uses regular expressions to extract the numeric part, making it robust against
    ///     various formats and trailing metadata.
    /// </summary>
    private static double? ParseReplayGainValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        // Matches a number optionally preceded by + or -
        var match = Regex.Match(value.Trim(), @"^[-+]?[0-9]*\.?[0-9]+");
        if (match.Success && double.TryParse(match.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result))
        {
            return result;
        }

        return null;
    }
}
