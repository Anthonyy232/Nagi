using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nagi.Models;
using Nagi.Services.Abstractions;
using TagLib;
using Nagi.Helpers;

namespace Nagi.Services.Implementations;

/// <summary>
/// Extracts music file metadata using the TagLib-Sharp library.
/// This implementation is thread-safe.
/// </summary>
public class TagLibMetadataExtractor : IMetadataExtractor {
    private const string UnknownArtistName = "Unknown Artist";
    private const string UnknownAlbumName = "Unknown Album";
    private readonly IFileSystemService _fileSystem;
    private readonly IImageProcessor _imageProcessor;

    // A dictionary of semaphores to prevent race conditions when saving album art.
    // Each unique album (identified by artist and title) gets its own lock. This allows
    // different albums to be processed in parallel, but prevents multiple threads from
    // writing the same album art file simultaneously.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _albumArtWriteSemaphores = new();

    public TagLibMetadataExtractor(IImageProcessor imageProcessor, IFileSystemService fileSystem) {
        _imageProcessor = imageProcessor ?? throw new ArgumentNullException(nameof(imageProcessor));
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
    }

    public async Task<SongFileMetadata> ExtractMetadataAsync(string filePath) {
        var metadata = new SongFileMetadata { FilePath = filePath };
        try {
            metadata.Title = _fileSystem.GetFileNameWithoutExtension(filePath);

            // Use a non-writable file abstraction to prevent accidental modification of the music file.
            using var tagFile = File.Create(new NonWritableFileAbstraction(filePath));

            var tag = tagFile.Tag;
            var props = tagFile.Properties;

            var artist = tag.Performers.FirstOrDefault()?.Trim() ?? UnknownArtistName;
            var albumArtist = tag.AlbumArtists.FirstOrDefault()?.Trim() ?? artist;

            metadata.Title = string.IsNullOrWhiteSpace(tag.Title) ? metadata.Title : tag.Title.Trim();
            metadata.Artist = artist;
            metadata.Album = tag.Album?.Trim() ?? UnknownAlbumName;
            metadata.AlbumArtist = albumArtist;
            metadata.Duration = props.Duration;
            metadata.Year = tag.Year > 0 ? (int)tag.Year : null;
            metadata.Genres = tag.Genres?.Where(g => !string.IsNullOrWhiteSpace(g)).Select(g => g.Trim()).ToList() ?? new List<string>();

            metadata.TrackNumber = tag.Track > 0 ? (int)tag.Track : null;
            metadata.TrackCount = tag.TrackCount > 0 ? (int)tag.TrackCount : null;
            metadata.DiscNumber = tag.Disc > 0 ? (int)tag.Disc : null;
            metadata.DiscCount = tag.DiscCount > 0 ? (int)tag.DiscCount : null;

            metadata.SampleRate = props.AudioSampleRate;
            metadata.Bitrate = props.AudioBitrate;
            metadata.Channels = props.AudioChannels;

            metadata.Lyrics = tag.Lyrics;
            metadata.Bpm = tag.BeatsPerMinute > 0 ? tag.BeatsPerMinute : null;
            metadata.Composer = tag.Composers.FirstOrDefault()?.Trim();
            metadata.Grouping = tag.Grouping?.Trim();
            metadata.Copyright = tag.Copyright?.Trim();
            metadata.Comment = tag.Comment?.Trim();
            metadata.Conductor = tag.Conductor?.Trim();
            metadata.MusicBrainzTrackId = tag.MusicBrainzTrackId;
            metadata.MusicBrainzReleaseId = tag.MusicBrainzReleaseId;

            var picture = tag.Pictures.FirstOrDefault();
            if (picture?.Data?.Data != null) {
                var albumTitle = metadata.Album ?? UnknownAlbumName;
                var artistNameForArt = metadata.AlbumArtist ?? UnknownArtistName;

                // Use a semaphore to ensure only one thread writes the cover art for this specific album at a time.
                var artKey = $"{artistNameForArt}_{albumTitle}";
                var semaphore = _albumArtWriteSemaphores.GetOrAdd(artKey, _ => new SemaphoreSlim(1, 1));

                await semaphore.WaitAsync();
                try {
                    (metadata.CoverArtUri, metadata.LightSwatchId, metadata.DarkSwatchId) =
                        await _imageProcessor.SaveCoverArtAndExtractColorsAsync(picture.Data.Data, albumTitle, artistNameForArt);
                }
                finally {
                    semaphore.Release();
                }
            }

            var fileInfo = _fileSystem.GetFileInfo(filePath);
            metadata.FileCreatedDate = fileInfo.CreationTimeUtc;
            metadata.FileModifiedDate = fileInfo.LastWriteTimeUtc;

            return metadata;
        }
        catch (CorruptFileException) {
            Trace.TraceWarning($"[MetadataExtractor] Corrupt file detected, skipping: '{filePath}'.");
            metadata.ExtractionFailed = true;
            metadata.ErrorMessage = "CorruptFile";
            return metadata;
        }
        catch (Exception ex) {
            Trace.TraceWarning(
                $"[MetadataExtractor] Metadata extraction failed for '{filePath}'. Reason: {ex.GetType().Name} - {ex.Message}");
            metadata.ExtractionFailed = true;
            metadata.ErrorMessage = ex.GetType().Name;
            return metadata;
        }
    }
}