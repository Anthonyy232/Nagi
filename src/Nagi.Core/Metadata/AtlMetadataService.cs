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
        // ATL splits ID3v2.3 artists on '/' by default, which divides "AC/DC".
        ATL.Settings.ID3v2_separatev2v3Values = false;
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
        var splitCharacters = _settingsService.Get(CoreSettings.ArtistSplitCharacters);
        var genreSplitCharacters = _settingsService.Get(CoreSettings.GenreSplitCharacters);

        try
        {
            var fileInfo = _fileSystem.GetFileInfo(filePath);
            metadata.FileCreatedDate = fileInfo.CreationTimeUtc;
            metadata.FileModifiedDate = fileInfo.LastWriteTimeUtc;
            metadata.Title = Path.GetFileNameWithoutExtension(filePath);

            var track = await ReadTrackAsync(filePath).ConfigureAwait(false);

            // Readable is a property of the format, not the file; ATL reports a damaged file as an unknown format.
            if (!track.AudioFormat.Readable || track.AudioFormat.ID == -1 ||
                track.AudioFormat.Name?.Equals("Unknown", StringComparison.OrdinalIgnoreCase) == true)
            {
                metadata.Failure = ExtractionFailure.UnsupportedFormat;
            }
            else
            {
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

        // A file that couldn't be read gets the names the library stores for missing tags, as a read one did above.
        if (metadata.Failure is not null) metadata.NormalizeTags(splitCharacters, genreSplitCharacters);
        return metadata;
    }

    private static void PopulateMetadataFromTrack(SongFileMetadata metadata, Track track, string splitCharacters, string genreSplitCharacters)
    {
        if (Text(track.Title) is { } title) metadata.Title = title;
        metadata.Artists = MultiValues(track.Artist);
        metadata.AlbumArtists = MultiValues(track.AlbumArtist);
        metadata.Album = Text(track.Album);
        metadata.Genres = MultiValues(track.Genre);
        metadata.ArtistSortNames = [.. MultiValues(track.SortArtist)];
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

        metadata.Composer = Text(track.Composer);
        metadata.Copyright = Text(track.Copyright);
        metadata.Comment = Text(track.Comment);
        metadata.Conductor = Text(track.Conductor);

        PopulateMusicBrainzIds(metadata, track);

        // ATL doesn't have a direct Grouping property, check additional fields
        if (track.AdditionalFields.TryGetValue("GRP1", out var grouping) ||
            track.AdditionalFields.TryGetValue("CONTENTGROUP", out grouping) ||
            track.AdditionalFields.TryGetValue("GROUPING", out grouping) ||
            track.AdditionalFields.TryGetValue("TIT1", out grouping))
            metadata.Grouping = Text(grouping);

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

    // ATL's separator, or null characters ATL leaves in ID3v2.3 frames. Split characters only divide a single value.
    private static List<string> MultiValues(string? tag) =>
        tag?.Split([AtlMultiValueSeparator, '\0']).Select(TextNormalizer.Normalize).OfType<string>().ToList() ?? [];

    // As foobar2000 shows several values in one field.
    private static string? Text(string? tag) => MultiValues(tag) is { Count: > 0 } values ? string.Join("; ", values) : null;

    private async Task<(string? Uri, string? Light, string? Dark)> ProcessAlbumArtAsync(
        string filePath, Track track, string? baseFolderPath)
    {
        var finder = new MediaFileFinder(_fileSystem, _logger);
        if (finder.FindCoverArt(filePath, baseFolderPath) is { } coverArtPath)
        {
            var directoryArt = await finder.SaveCoverArtAsync(coverArtPath, _imageProcessor).ConfigureAwait(false);
            if (directoryArt.Uri != null) return directoryArt;
        }

        var picture = track.EmbeddedPictures?.FirstOrDefault();
        return picture?.PictureData is { Length: > 0 } pictureData
            ? await _imageProcessor.SaveCoverArtAndExtractColorsAsync(pictureData).ConfigureAwait(false)
            : default;
    }

    public async Task<string?> GetLyricsPathAsync(string filePath)
    {
        // The tags name the cached lyrics, so they're read as a scan reads them.
        var metadata = await ExtractMetadataAsync(filePath, includeMediaAssets: false).ConfigureAwait(false);
        if (metadata.Failure is not null) return null;

        try
        {
            var track = await ReadTrackAsync(filePath).ConfigureAwait(false);
            return await GetLrcPathAsync(filePath, metadata.FileModifiedDate!.Value, metadata.Artists[0], metadata.Album,
                metadata.Title, track).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read the lyrics of {FilePath}.", filePath);
            return null;
        }
    }

    // ATL parsing is synchronous, so the timeout is enforced while awaiting it.
    private static Task<Track> ReadTrackAsync(string filePath) =>
        Task.Run(() => new Track(filePath)).WaitAsync(TimeSpan.FromSeconds(30));

    // The user's override wins; lyrics extracted from the tags are reused until the audio file changes.
    private async Task<string?> GetLrcPathAsync(string audioFilePath, DateTime audioFileLastWriteTime, string? artist, string? album, string? title, Track track)
    {
        var overridePath = LrcCache.OverridePath(_pathConfig.LrcCachePath, audioFilePath);
        if (_fileSystem.FileExists(overridePath)) return overridePath;

        if (new MediaFileFinder(_fileSystem, _logger).FindSidecarLyrics(audioFilePath) is { } sidecarPath) return sidecarPath;

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
