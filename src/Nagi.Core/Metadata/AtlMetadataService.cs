using System.Globalization;
using System.Text.RegularExpressions;
using ATL;
using Microsoft.Extensions.Logging;
using Nagi.Core.Artwork;
using Nagi.Core.Library;
using Nagi.Core.Lyrics;
using Nagi.Core.Platform;
using Nagi.Core.Settings;

namespace Nagi.Core.Metadata;

public partial class AtlMetadataService : IMetadataService
{
    private const char AtlMultiValueSeparator = '\u001F';
    private const char CombinedValueSeparator = ';';
    private const string ReplayGainTrackGainKey = "REPLAYGAIN_TRACK_GAIN";
    private const string ReplayGainTrackPeakKey = "REPLAYGAIN_TRACK_PEAK";
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
            metadata.Title = Path.GetFileNameWithoutExtension(filePath);

            // ATL parsing is synchronous, so enforce the timeout while awaiting it.
            var track = await Task.Run(() => new Track(filePath)).WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);

            // Readable is a property of the format, not the file; ATL reports a damaged file as an unknown format.
            if (!track.AudioFormat.Readable || track.AudioFormat.ID == -1 ||
                track.AudioFormat.Name?.Equals("Unknown", StringComparison.OrdinalIgnoreCase) == true)
            {
                metadata.Failure = ExtractionFailure.UnsupportedFormat;
                return metadata;
            }

            var splitCharacters = _settingsService.Get(CoreSettings.ArtistSplitCharacters);
            var genreSplitCharacters = _settingsService.Get(CoreSettings.GenreSplitCharacters);
            PopulateMetadataFromTrack(metadata, track, splitCharacters, genreSplitCharacters);

            if (includeMediaAssets)
            {
                try
                {
                    metadata.LrcFilePath = await GetLrcPathAsync(filePath, fileInfo.LastWriteTimeUtc,
                            metadata.Artists[0], metadata.Album,
                            metadata.Title, track)
                        .WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    _logger.LogWarning("LRC extraction timed out for file: {FilePath}", filePath);
                }

                if (string.IsNullOrWhiteSpace(metadata.LrcFilePath))
                    metadata.LrcFilePath = new MediaFileFinder(_fileSystem, _logger).FindSidecarLyrics(filePath);
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
            metadata.Failure = ExtractionFailure.Timeout;
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "File access error during metadata extraction for '{FilePath}'.", filePath);
            metadata.Failure = ExtractionFailure.FileAccess;
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "Access denied during metadata extraction for '{FilePath}'.", filePath);
            metadata.Failure = ExtractionFailure.AccessDenied;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An unexpected error occurred during metadata extraction for '{FilePath}'.",
                filePath);
            metadata.Failure = ExtractionFailure.Unexpected;
        }

        return metadata;
    }

    private static void PopulateMetadataFromTrack(SongFileMetadata metadata, Track track, string splitCharacters, string genreSplitCharacters)
    {
        if (!string.IsNullOrWhiteSpace(track.Title)) metadata.Title = track.Title;
        metadata.Artists = MultiValues(track.Artist, splitCharacters);
        metadata.AlbumArtists = MultiValues(track.AlbumArtist, splitCharacters);
        metadata.Album = track.Album;
        metadata.Genres = MultiValues(track.Genre, genreSplitCharacters);
        metadata.NormalizeTags(splitCharacters, genreSplitCharacters);

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

        var lyrics = TextNormalizer.Normalize(track.Lyrics?.FirstOrDefault()?.UnsynchronizedLyrics);
        metadata.Lyrics = lyrics is null
            ? null
            : TextNormalizer.Normalize(LyricsLanguagePrefix().Replace(lyrics, string.Empty));

        metadata.Composer = TextNormalizer.Normalize(track.Composer);
        metadata.Copyright = TextNormalizer.Normalize(track.Copyright);
        metadata.Comment = TextNormalizer.Normalize(track.Comment);
        metadata.Conductor = TextNormalizer.Normalize(track.Conductor);

        PopulateMusicBrainzIds(metadata, track);

        // ATL doesn't have a direct Grouping property, check additional fields
        if (track.AdditionalFields.TryGetValue("GRP1", out var grouping) ||
            track.AdditionalFields.TryGetValue("CONTENTGROUP", out grouping) ||
            track.AdditionalFields.TryGetValue("GROUPING", out grouping) ||
            track.AdditionalFields.TryGetValue("TIT1", out grouping))
            metadata.Grouping = TextNormalizer.Normalize(grouping);

        metadata.ReplayGainTrackGain = ParseReplayGainValue(GetAdditionalField(track, ReplayGainTrackGainKey));
        metadata.ReplayGainTrackPeak = ParseReplayGainValue(GetAdditionalField(track, ReplayGainTrackPeakKey));
    }

    private static void PopulateMusicBrainzIds(SongFileMetadata metadata, Track track)
    {
        foreach (var (key, value) in track.AdditionalFields)
        {
            if (key != "UFID" && !key.Contains("MusicBrainz", StringComparison.OrdinalIgnoreCase)) continue;

            // Each format spells these keys differently (MUSICBRAINZ_ALBUMID, "MusicBrainz Album Id",
            // "MusicBrainz/Album Id"), and Picard writes the MP3 recording ID to a UFID frame.
            var name = key == "UFID" && value.StartsWith("http://musicbrainz.org", StringComparison.OrdinalIgnoreCase)
                ? "MUSICBRAINZTRACKID"
                : string.Concat(key.Where(char.IsLetter)).ToUpperInvariant();
            switch (name)
            {
                case "MUSICBRAINZTRACKID":
                    metadata.MusicBrainzTrackId ??= ParseMusicBrainzIds(value).FirstOrDefault();
                    break;
                case "MUSICBRAINZALBUMID" or "MUSICBRAINZRELEASEID":
                    metadata.MusicBrainzReleaseId ??= ParseMusicBrainzIds(value).FirstOrDefault();
                    break;
                case "MUSICBRAINZRELEASEGROUPID":
                    metadata.MusicBrainzReleaseGroupId ??= ParseMusicBrainzIds(value).FirstOrDefault();
                    break;
                case "MUSICBRAINZARTISTID":
                    metadata.MusicBrainzArtistIds = ParseMusicBrainzIds(value);
                    break;
                case "MUSICBRAINZALBUMARTISTID":
                    metadata.MusicBrainzAlbumArtistIds = ParseMusicBrainzIds(value);
                    break;
            }
        }
    }

    // Multi-artist IDs arrive joined by whatever separator the tagger chose, so match the IDs themselves.
    private static List<string> ParseMusicBrainzIds(string value) =>
        MusicBrainzIdRegex().Matches(value).Select(match => match.Value.ToLowerInvariant()).ToList();

    // ATL joins a tag's values with its separator. Without split characters they stay one name joined by ';', which
    // is how files' names have always been stored, while a server's lists of names stay separate names.
    private static List<string> MultiValues(string? tag, string splitCharacters)
    {
        // Normalized first, so null characters become a separator that split characters can divide.
        if (TextNormalizer.Normalize(tag) is not { } value) return [];
        return splitCharacters.Length == 0
            ? [value.Replace(AtlMultiValueSeparator, CombinedValueSeparator)]
            : [.. value.Split(AtlMultiValueSeparator)];
    }

    private async Task<(string? Uri, string? Light, string? Dark)> ProcessAlbumArtAsync(
        string filePath, Track track, string? baseFolderPath)
    {
        var directoryArt = await ProcessCoverArtFromDirectoryAsync(filePath, baseFolderPath).ConfigureAwait(false);
        if (directoryArt.Uri != null) return directoryArt;

        var picture = track.EmbeddedPictures?.FirstOrDefault();
        return picture?.PictureData is { Length: > 0 } pictureData
            ? await _imageProcessor.SaveCoverArtAndExtractColorsAsync(pictureData).ConfigureAwait(false)
            : default;
    }

    private async Task<(string? Uri, string? Light, string? Dark)> ProcessCoverArtFromDirectoryAsync(
        string filePath, string? baseFolderPath)
    {
        try
        {
            var coverArtPath = new MediaFileFinder(_fileSystem, _logger).FindCoverArt(filePath, baseFolderPath);
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

    // The user's override wins; lyrics extracted from the tags are reused until the audio file changes.
    private async Task<string?> GetLrcPathAsync(string audioFilePath, DateTime audioFileLastWriteTime, string? artist, string? album, string? title, Track track)
    {
        var overridePath = LrcCache.OverridePath(_pathConfig.LrcCachePath, audioFilePath);
        if (_fileSystem.FileExists(overridePath)) return overridePath;

        var cachedLrcPath = LrcCache.CachedPath(_pathConfig.LrcCachePath, audioFilePath, artist, album, title);

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

    private async Task<string?> ExtractAndCacheEmbeddedLrcAsync(Track track, string cachedLrcPath)
    {
        var lyricsInfo = track.Lyrics?.FirstOrDefault();
        if (lyricsInfo == null) return null;

        var syncLyrics = lyricsInfo.SynchronizedLyrics;
        if (syncLyrics == null || syncLyrics.Count == 0) return null;

        var lrcContent = LyricsFormatter.Format(syncLyrics
            .Select(phrase => ((TimeSpan?)TimeSpan.FromMilliseconds(phrase.TimestampStart), (string?)phrase.Text)).ToList());
        if (lrcContent is null) return null;

        await LyricsStorageHelper.SaveAsync(_fileSystem, cachedLrcPath, lrcContent).ConfigureAwait(false);
        return cachedLrcPath;
    }

    public void WriteReplayGain(string filePath, double gainDb, double peak)
    {
        try
        {
            var track = new Track(filePath);
            track.AdditionalFields[ReplayGainTrackGainKey] = FormattableString.Invariant($"{gainDb:F2} dB");
            track.AdditionalFields[ReplayGainTrackPeakKey] = FormattableString.Invariant($"{peak:F6}");
            if (!track.Save()) _logger.LogError("Failed to save ReplayGain tags to file: {FilePath}", filePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error writing ReplayGain tags to file: {FilePath}", filePath);
        }
    }

    private static string? GetAdditionalField(Track track, string key)
    {
        var storedKey = track.AdditionalFields.Keys.FirstOrDefault(k => k.Equals(key, StringComparison.OrdinalIgnoreCase));
        return storedKey is null ? null : track.AdditionalFields[storedKey];
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

    // ATL prefixes unsynchronized lyrics with their language, as in "eng||".
    [GeneratedRegex(@"^[a-z]{2,3}\|\|", RegexOptions.IgnoreCase)]
    private static partial Regex LyricsLanguagePrefix();

    [GeneratedRegex(@"^[-+]?(?:[0-9]+(?:\.[0-9]*)?|\.[0-9]+)(?:[eE][-+]?[0-9]+)?")]
    private static partial Regex ReplayGainNumberRegex();

    [GeneratedRegex("[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}", RegexOptions.IgnoreCase)]
    private static partial Regex MusicBrainzIdRegex();
}
