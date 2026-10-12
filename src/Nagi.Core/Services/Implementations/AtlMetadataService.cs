using System.Globalization;
using System.Text.RegularExpressions;
using ATL;
using Microsoft.Extensions.Logging;
using ModernLrc;
using Nagi.Core.Constants;
using Nagi.Core.Helpers;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;

namespace Nagi.Core.Services.Implementations;

public partial class AtlMetadataService : IMetadataService
{
    private const char AtlMultiValueSeparator = '\u001F';
    private const char CombinedValueSeparator = ';';
    private readonly IFileSystemService _fileSystem;
    private readonly IImageProcessor _imageProcessor;
    private readonly ILogger<AtlMetadataService> _logger;
    private readonly IPathConfiguration _pathConfig;
    private readonly ISettingsService _settingsService;

    static AtlMetadataService()
    {
        ATL.Settings.DisplayValueSeparator = AtlMultiValueSeparator;
        ATL.Settings.FileBufferSize = 8 * 1024;
    }

    public AtlMetadataService(IImageProcessor imageProcessor, IFileSystemService fileSystem,
        IPathConfiguration pathConfig, ILogger<AtlMetadataService> logger, ISettingsService settingsService)
    {
        _imageProcessor = imageProcessor;
        _fileSystem = fileSystem;
        _pathConfig = pathConfig;
        _logger = logger;
        _settingsService = settingsService;
    }

    public async Task<SongFileMetadata> ExtractMetadataAsync(string filePath, string? baseFolderPath = null,
        bool includeMediaAssets = true)
    {
        var metadata = new SongFileMetadata { FilePath = filePath };

        try
        {
            var fileInfo = _fileSystem.GetFileInfo(filePath);
            metadata.FileCreatedDate = fileInfo.CreationTimeUtc;
            metadata.FileModifiedDate = fileInfo.LastWriteTimeUtc;
            metadata.Title = ArtistNameHelper.NormalizeStringCore(_fileSystem.GetFileNameWithoutExtension(filePath)) ?? _fileSystem.GetFileNameWithoutExtension(filePath);

            // ATL parsing is synchronous, so enforce the timeout while awaiting it.
            var track = await Task.Run(() => new Track(filePath)).WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);

            var isUnknownFormat = track.AudioFormat.Name?.Equals("Unknown", StringComparison.OrdinalIgnoreCase) == true ||
                                  track.AudioFormat.ID == -1;

            if (!track.AudioFormat.Readable || isUnknownFormat)
            {
                metadata.ExtractionFailed = true;
                metadata.ErrorMessage = isUnknownFormat ? "UnsupportedFormat" : "CorruptFile";
                return metadata;
            }

            var splitCharacters = await _settingsService.GetArtistSplitCharactersAsync().ConfigureAwait(false);
            var genreSplitCharacters = await _settingsService.GetGenreSplitCharactersAsync().ConfigureAwait(false);
            PopulateMetadataFromTrack(metadata, track, splitCharacters, genreSplitCharacters);

            if (includeMediaAssets)
            {
                try
                {
                    metadata.LrcFilePath = await GetLrcPathAsync(filePath, fileInfo.LastWriteTimeUtc,
                            metadata.Artists?.FirstOrDefault() ?? Artist.UnknownArtistName, metadata.Album,
                            metadata.Title, track)
                        .WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    _logger.LogWarning("LRC extraction timed out for file: {FilePath}", filePath);
                }

                if (string.IsNullOrWhiteSpace(metadata.LrcFilePath))
                    metadata.LrcFilePath = LyricsStorageHelper.FindSidecar(_fileSystem, filePath, _logger);
                try
                {
                    (metadata.CoverArtUri, metadata.LightSwatchId, metadata.DarkSwatchId) =
                        await ProcessAlbumArtAsync(filePath, track, baseFolderPath)
                            .WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    _logger.LogWarning("Album art extraction timed out for file: {FilePath}", filePath);
                }
            }
        }
        catch (TimeoutException)
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

    private static void PopulateMetadataFromTrack(SongFileMetadata metadata, Track track, string splitCharacters, string genreSplitCharacters)
    {
        var rawArtist = ArtistNameHelper.NormalizeStringCore(track.Artist) ?? Artist.UnknownArtistName;
        var rawAlbumArtist = ArtistNameHelper.NormalizeStringCore(track.AlbumArtist) ?? rawArtist;

        var artists = SplitMetadataList(rawArtist, splitCharacters);
        var albumArtists = SplitMetadataList(rawAlbumArtist, splitCharacters);

        var album = ArtistNameHelper.NormalizeStringCore(track.Album) ?? Album.UnknownAlbumName;

        metadata.Title = ArtistNameHelper.NormalizeStringCore(track.Title) ?? metadata.Title;
        metadata.Artists = artists;
        metadata.Album = album;
        metadata.AlbumArtists = albumArtists;
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

        metadata.Lyrics = ArtistNameHelper.NormalizeStringCore(track.Lyrics?.FirstOrDefault()?.UnsynchronizedLyrics);

        metadata.Composer = ArtistNameHelper.NormalizeStringCore(track.Composer);
        metadata.Copyright = ArtistNameHelper.NormalizeStringCore(track.Copyright);
        metadata.Comment = ArtistNameHelper.NormalizeStringCore(track.Comment);
        metadata.Conductor = ArtistNameHelper.NormalizeStringCore(track.Conductor);

        // ATL uses AdditionalFields for MusicBrainz IDs
        if (track.AdditionalFields.TryGetValue("MUSICBRAINZ_TRACKID", out var mbTrackId))
            metadata.MusicBrainzTrackId = ArtistNameHelper.NormalizeStringCore(mbTrackId);
        if (track.AdditionalFields.TryGetValue("MUSICBRAINZ_RELEASEID", out var mbReleaseId) ||
            track.AdditionalFields.TryGetValue("MUSICBRAINZ_ALBUMID", out mbReleaseId))
            metadata.MusicBrainzReleaseId = ArtistNameHelper.NormalizeStringCore(mbReleaseId);

        // Parse genre (ATL returns a single string, may need to split)
        var genreString = ArtistNameHelper.NormalizeStringCore(track.Genre);
        metadata.Genres = SplitMetadataList(genreString, genreSplitCharacters);

        // ATL doesn't have a direct Grouping property, check additional fields
        if (track.AdditionalFields.TryGetValue("GRP1", out var grouping) ||
            track.AdditionalFields.TryGetValue("CONTENTGROUP", out grouping) ||
            track.AdditionalFields.TryGetValue("GROUPING", out grouping) ||
            track.AdditionalFields.TryGetValue("TIT1", out grouping))
            metadata.Grouping = ArtistNameHelper.NormalizeStringCore(grouping);

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

    private static List<string> SplitMetadataList(string? input, string splitCharacters)
    {
        if (string.IsNullOrWhiteSpace(input)) return [];

        if (string.IsNullOrEmpty(splitCharacters))
        {
            var normalized = ArtistNameHelper.NormalizeStringCore(
                input.Replace(AtlMultiValueSeparator, CombinedValueSeparator));
            return normalized != null ? [normalized] : [];
        }

        return input
            .Split([.. splitCharacters, AtlMultiValueSeparator], StringSplitOptions.RemoveEmptyEntries)
            .Select(s => ArtistNameHelper.NormalizeStringCore(s))
            .Where(s => !string.IsNullOrEmpty(s))
            .Select(s => s!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<(string? Uri, string? Light, string? Dark)> ProcessAlbumArtAsync(
        string filePath, Track track, string? baseFolderPath)
    {
        var directoryArt = await ProcessCoverArtFromDirectoryAsync(filePath, baseFolderPath).ConfigureAwait(false);
        if (directoryArt.Uri != null) return directoryArt;

        // No directory art found — fall back to embedded image.
        var picture = track.EmbeddedPictures?.FirstOrDefault();
        if (picture?.PictureData is { Length: > 0 } pictureData)
        {
            try
            {
                return await _imageProcessor.SaveCoverArtAndExtractColorsAsync(pictureData).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process embedded album art for '{FilePath}'.", filePath);
            }
        }

        return default;
    }

    /// <summary>
    ///     Searches for cover art files in the directory hierarchy, starting from the song's
    ///     directory and walking up to the base folder path.
    /// </summary>
    private async Task<(string? Uri, string? Light, string? Dark)> ProcessCoverArtFromDirectoryAsync(
        string filePath, string? baseFolderPath)
    {
        try
        {
            var coverArtPath = FindCoverArtInDirectoryHierarchy(filePath, baseFolderPath);
            if (string.IsNullOrEmpty(coverArtPath)) return default;

            var imageBytes = await _fileSystem.ReadAllBytesAsync(coverArtPath).ConfigureAwait(false);
            if (imageBytes.Length == 0) return default;

            var result =
                await _imageProcessor.SaveCoverArtAndExtractColorsAsync(imageBytes).ConfigureAwait(false);

            _logger.LogDebug("Found cover art from directory for '{FilePath}': {CoverArtPath}",
                filePath, coverArtPath);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to process directory cover art for '{FilePath}'.", filePath);
            return default;
        }
    }

    /// <summary>
    ///     Searches for a cover art file in the directory hierarchy, starting from the song's
    ///     directory and walking up to the base folder path. Looks for common cover art file
    ///     names (cover, folder, album, front) with supported image extensions.
    /// </summary>
    /// <param name="baseFolderPath">The root folder path to stop searching at.</param>
    /// <returns>The full path to the first matching cover art file, or null if none found.</returns>
    private string? FindCoverArtInDirectoryHierarchy(string songFilePath, string? baseFolderPath)
    {
        try
        {
            var currentDirectory = _fileSystem.GetDirectoryName(songFilePath);
            if (string.IsNullOrEmpty(currentDirectory)) return null;

            var normalizedBasePath = baseFolderPath?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            while (!string.IsNullOrEmpty(currentDirectory))
            {
                var coverArtPath = ImageStorageHelper.FindCoverArtInDirectory(_fileSystem, currentDirectory, _logger);
                if (coverArtPath != null) return coverArtPath;

                var normalizedCurrent = currentDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (!string.IsNullOrEmpty(normalizedBasePath) &&
                    normalizedCurrent.Equals(normalizedBasePath, StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                currentDirectory = _fileSystem.GetDirectoryName(currentDirectory);
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
    ///     Gets the path to the LRC file, prioritizing a valid cache entry before
    ///     attempting to extract embedded lyrics from the audio file.
    /// </summary>
    private async Task<string?> GetLrcPathAsync(string audioFilePath, DateTime audioFileLastWriteTime, string? artist, string? album, string? title, Track track)
    {
        var overrideFileName = FileNameHelper.GenerateLrcOverrideFileName(audioFilePath);
        var overridePath = _fileSystem.Combine(_pathConfig.LrcCachePath, overrideFileName);
        if (_fileSystem.FileExists(overridePath)) return overridePath;

        var cacheFileName = FileNameHelper.GenerateLrcCacheFileName(audioFilePath, artist, album, title);
        var cachedLrcPath = _fileSystem.Combine(_pathConfig.LrcCachePath, cacheFileName);

        // Check for a valid cache entry. It's valid if it exists and is newer than the audio file.
        if (_fileSystem.FileExists(cachedLrcPath))
        {
            var cacheLastWriteTime = _fileSystem.GetLastWriteTimeUtc(cachedLrcPath);
            if (cacheLastWriteTime >= audioFileLastWriteTime) return cachedLrcPath;
        }

        try
        {
            return await ExtractAndCacheEmbeddedLrcAsync(track, cachedLrcPath).ConfigureAwait(false);
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

        var builder = new LrcDocumentBuilder();

        foreach (var phrase in syncLyrics)
        {
            var text = ArtistNameHelper.NormalizeStringCore(phrase.Text);
            if (string.IsNullOrWhiteSpace(text)) continue;

            var time = TimeSpan.FromMilliseconds(phrase.TimestampStart);
            builder.AddLine(time, text);
        }

        if (builder.LineCount == 0) return null;
        var lrcContent = LrcWriter.Write(builder.Build(), new LrcWriteOptions
        {
            TimestampPrecision = LrcTimestampPrecision.Milliseconds
        });

        await LyricsStorageHelper.SaveAsync(_fileSystem, cachedLrcPath, lrcContent).ConfigureAwait(false);
        return cachedLrcPath;
    }

    private static double? ParseReplayGainValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var match = ReplayGainNumberRegex().Match(value.Trim());
        if (match.Success && double.TryParse(match.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result))
        {
            return result;
        }

        return null;
    }

    [GeneratedRegex(@"^[-+]?(?:[0-9]+(?:\.[0-9]*)?|\.[0-9]+)(?:[eE][-+]?[0-9]+)?")]
    private static partial Regex ReplayGainNumberRegex();
}
