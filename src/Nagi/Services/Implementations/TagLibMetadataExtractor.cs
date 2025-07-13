// Nagi/Services/Implementations/TagLibMetadataExtractor.cs

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Nagi.Models;
using Nagi.Services.Abstractions;
using TagLib;
using Nagi.Helpers;

namespace Nagi.Services.Implementations;

/// <summary>
/// Extracts music file metadata using the TagLib-Sharp library.
/// </summary>
public class TagLibMetadataExtractor : IMetadataExtractor {
    private const string UnknownArtistName = "Unknown Artist";
    private const string UnknownAlbumName = "Unknown Album";
    private readonly IFileSystemService _fileSystem;
    private readonly IImageProcessor _imageProcessor;

    public TagLibMetadataExtractor(IImageProcessor imageProcessor, IFileSystemService fileSystem) {
        _imageProcessor = imageProcessor ?? throw new ArgumentNullException(nameof(imageProcessor));
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
    }

    public Task<SongFileMetadata> ExtractMetadataAsync(string filePath) {
        return Task.Run(async () => {
            var metadata = new SongFileMetadata { FilePath = filePath };
            try {
                metadata.Title = _fileSystem.GetFileNameWithoutExtension(filePath);
                IPicture? picture = null;

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
                metadata.Genres =
                    tag.Genres?.Where(g => !string.IsNullOrWhiteSpace(g)).Select(g => g.Trim()).ToList() ??
                    new List<string>();

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

                picture = tag.Pictures.FirstOrDefault();

                if (picture?.Data?.Data != null) {
                    var albumTitle = metadata.Album ?? UnknownAlbumName;
                    var artistNameForArt = metadata.AlbumArtist ?? UnknownArtistName;

                    (metadata.CoverArtUri, metadata.LightSwatchId, metadata.DarkSwatchId) =
                        await _imageProcessor.SaveCoverArtAndExtractColorsAsync(picture.Data.Data, albumTitle, artistNameForArt);
                }

                var fileInfo = _fileSystem.GetFileInfo(filePath);
                metadata.FileCreatedDate = fileInfo.CreationTimeUtc;
                metadata.FileModifiedDate = fileInfo.LastWriteTimeUtc;

                return metadata;
            }
            catch (Exception ex) {
                Debug.WriteLine(
                    $"[MetadataExtractor] Warning: Metadata extraction failed for '{filePath}'. Reason: {ex.GetType().Name} - {ex.Message}");
                metadata.ExtractionFailed = true;
                metadata.ErrorMessage = ex.GetType().Name;
                return metadata;
            }
        });
    }
}