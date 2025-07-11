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
        //
        // Offload synchronous file I/O to a background thread to prevent blocking the caller.
        // The lambda is marked async to allow awaiting the asynchronous image processing call.
        return Task.Run(async () => {
            var metadata = new SongFileMetadata { FilePath = filePath };
            try {
                metadata.Title = _fileSystem.GetFileNameWithoutExtension(filePath);
                IPicture? picture = null;

                using var tagFile = File.Create(new NonWritableFileAbstraction(filePath));

                var tag = tagFile.Tag;
                var props = tagFile.Properties;

                metadata.Title = string.IsNullOrWhiteSpace(tag.Title) ? metadata.Title : tag.Title.Trim();
                metadata.Artist = tag.Performers.FirstOrDefault()?.Trim() ?? UnknownArtistName;
                metadata.Album = tag.Album?.Trim() ?? UnknownAlbumName;
                metadata.AlbumArtist = tag.AlbumArtists.FirstOrDefault()?.Trim() ?? metadata.Artist;
                metadata.Duration = props.Duration;
                metadata.Year = tag.Year > 0 ? (int)tag.Year : null;
                metadata.Genres =
                    tag.Genres?.Where(g => !string.IsNullOrWhiteSpace(g)).Select(g => g.Trim()).ToList() ??
                    new List<string>();
                metadata.TrackNumber = tag.Track > 0 ? (int)tag.Track : null;
                metadata.DiscNumber = tag.Disc > 0 ? (int)tag.Disc : null;
                metadata.SampleRate = props.AudioSampleRate;
                metadata.Bitrate = props.AudioBitrate;
                metadata.Channels = props.AudioChannels;
                picture = tag.Pictures.FirstOrDefault();

                //
                // After synchronous metadata is read, process the album art asynchronously.
                if (picture?.Data?.Data != null) {
                    var albumTitle = metadata.Album ?? UnknownAlbumName;
                    var artistName = metadata.AlbumArtist ?? UnknownArtistName;

                    (metadata.CoverArtUri, metadata.LightSwatchId, metadata.DarkSwatchId) =
                        await _imageProcessor.SaveCoverArtAndExtractColorsAsync(picture.Data.Data, albumTitle, artistName);
                }

                var fileInfo = _fileSystem.GetFileInfo(filePath);
                metadata.FileCreatedDate = fileInfo.CreationTimeUtc;
                metadata.FileModifiedDate = fileInfo.LastWriteTimeUtc;

                return metadata;
            }
            catch (Exception ex) {
                //
                // Log extraction failures but allow the process to continue with other files.
                Debug.WriteLine(
                    $"[MetadataExtractor] Warning: Metadata extraction failed for '{filePath}'. Reason: {ex.GetType().Name} - {ex.Message}");
                metadata.ExtractionFailed = true;
                metadata.ErrorMessage = ex.GetType().Name;
                return metadata;
            }
        });
    }
}