using ATL;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Nagi.Core.Helpers;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Implementations;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Nagi.Core.Tests;

/// <summary>
///     Contains unit tests for the <see cref="AtlMetadataService" />.
///     These tests verify the service's ability to extract metadata, handle album art,
///     manage lyrics, and gracefully handle various file-related errors.
///     The tests utilize a real temporary file system for ATL.NET interactions,
///     while all other external dependencies like image processing and file system access
///     are mocked using NSubstitute.
/// </summary>
public class AtlMetadataServiceTests : IDisposable
{
    private const string LrcCachePath = "C:\\cache\\lrc";
    private readonly IFileSystemService _fileSystem;
    private readonly IImageProcessor _imageProcessor;
    private readonly ILogger<AtlMetadataService> _logger;
    private readonly AtlMetadataService _metadataService;
    private readonly IPathConfiguration _pathConfig;
    private readonly string _tempDirectory;

    // A valid MP3 file with proper headers - this is the minimum valid MP3 structure 
    // that ATL.NET can read and write tags to
    private static readonly byte[] ValidMp3Bytes = CreateValidMp3Bytes();

    public AtlMetadataServiceTests()
    {
        _imageProcessor = Substitute.For<IImageProcessor>();
        _fileSystem = Substitute.For<IFileSystemService>();
        _pathConfig = Substitute.For<IPathConfiguration>();
        _logger = Substitute.For<ILogger<AtlMetadataService>>();

        _tempDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempDirectory);

        _pathConfig.LrcCachePath.Returns(LrcCachePath);

        _fileSystem.GetFileNameWithoutExtension(Arg.Any<string>())
            .Returns(callInfo => Path.GetFileNameWithoutExtension(callInfo.ArgAt<string>(0)) ??
                                 throw new InvalidOperationException("Value cannot be null"));
        _fileSystem.GetDirectoryName(Arg.Any<string>())
            .Returns(callInfo => Path.GetDirectoryName(callInfo.ArgAt<string>(0)) ??
                                 throw new InvalidOperationException("Value cannot be null"));
        _fileSystem.Combine(Arg.Any<string[]>())
            .Returns(callInfo => Path.Combine(callInfo.ArgAt<string[]>(0)));
        _fileSystem.GetExtension(Arg.Any<string>())
            .Returns(callInfo => Path.GetExtension(callInfo.ArgAt<string>(0)));

        _metadataService = new AtlMetadataService(_imageProcessor, _fileSystem, _pathConfig, _logger);
    }

    /// <summary>
    ///     Creates a minimal valid MP3 file with proper structure for ATL.NET.
    /// </summary>
    private static byte[] CreateValidMp3Bytes()
    {
        // Create a minimal but valid MP3 file structure
        // This includes an ID3v2 header followed by a minimal audio frame
        using var ms = new MemoryStream();
        
        // ID3v2.3 header (10 bytes)
        ms.Write(new byte[] { 0x49, 0x44, 0x33 }); // "ID3"
        ms.Write(new byte[] { 0x03, 0x00 }); // Version 2.3
        ms.Write(new byte[] { 0x00 }); // Flags
        ms.Write(new byte[] { 0x00, 0x00, 0x00, 0x00 }); // Size (syncsafe, 0 for now)
        
        // Add some minimal MP3 audio frames (silent frames)
        // MP3 frame header: sync word + audio info
        for (var i = 0; i < 10; i++)
        {
            // MP3 frame sync (0xFFE for MPEG Audio Layer 3)
            ms.Write(new byte[] { 0xFF, 0xFB, 0x90, 0x00 }); // MPEG1 Layer3, 128kbps, 44100Hz
            // Fill with zeros for the rest of the frame (417 bytes for this config)
            ms.Write(new byte[413]);
        }
        
        return ms.ToArray();
    }

    public void Dispose()
    {
        // Force garbage collection to release any file handles held by ATL or FileInfo
        GC.Collect();
        GC.WaitForPendingFinalizers();

        try
        {
            if (Directory.Exists(_tempDirectory)) Directory.Delete(_tempDirectory, true);
        }
        catch (IOException ex)
        {
            Console.WriteLine($"Could not delete temp directory {_tempDirectory}. Exception: {ex.Message}");
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>
    ///     Creates a temporary, valid MP3 audio file with custom tags for testing purposes.
    /// </summary>
    /// <param name="fileName">The name of the file to create within the temporary directory.</param>
    /// <param name="trackSetup">An action to configure the tags on the created <see cref="Track" /> object.</param>
    /// <returns>The full path to the newly created temporary audio file.</returns>
    private string CreateTestAudioFile(string fileName, Action<Track> trackSetup)
    {
        var filePath = Path.Combine(_tempDirectory, fileName);
        File.WriteAllBytes(filePath, ValidMp3Bytes);

        var track = new Track(filePath);
        trackSetup(track);
        track.Save();

        _fileSystem.GetFileInfo(filePath).Returns(new FileInfo(filePath));
        _fileSystem.FileExists(filePath).Returns(true);

        return filePath;
    }

    /// <summary>
    ///     Verifies that the service correctly extracts all standard metadata properties
    ///     from a well-tagged audio file.
    /// </summary>
    [Fact]
    public async Task ExtractMetadataAsync_WithFullMetadata_ExtractsAllPropertiesCorrectly()
    {
        // Arrange
        var pictureData = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }; // PNG header
        var filePath = CreateTestAudioFile("full.mp3", track =>
        {
            track.Title = "Test Title";
            track.Artist = "Test Artist";
            track.AlbumArtist = "Test Album Artist";
            track.Album = "Test Album";
            track.Year = 2023;
            track.TrackNumber = 5;
            track.TrackTotal = 10;
            track.DiscNumber = 1;
            track.DiscTotal = 2;
            track.BPM = 120;
            track.Lyrics.Add(new LyricsInfo { UnsynchronizedLyrics = "Some simple lyrics" });
            track.Genre = "Rock;Pop";
            track.AdditionalFields.Add("GROUPING", "Test Grouping");
            track.EmbeddedPictures.Add(PictureInfo.fromBinaryData(pictureData));
        });

        _imageProcessor.SaveCoverArtAndExtractColorsAsync(Arg.Any<byte[]>())
            .Returns(Task.FromResult<(string?, string?, string?)>(("C:/art/1.jpg", "light1", "dark1")));

        // Act
        var result = await _metadataService.ExtractMetadataAsync(filePath);

        // Assert
        result.Should().NotBeNull();
        result.ExtractionFailed.Should().BeFalse();
        result.Title.Should().Be("Test Title");
        result.Artist.Should().Be("Test Artist");
        result.AlbumArtist.Should().Be("Test Album Artist");
        result.Album.Should().Be("Test Album");
        result.Year.Should().Be(2023);
        result.TrackNumber.Should().Be(5);
        result.TrackCount.Should().Be(10);
        result.DiscNumber.Should().Be(1);
        result.DiscCount.Should().Be(2);
        result.Bpm.Should().Be(120);
        result.Lyrics.Should().Be("Some simple lyrics");
        result.Genres.Should().ContainInOrder("Rock", "Pop");
        result.CoverArtUri.Should().Be("C:/art/1.jpg");
        result.Grouping.Should().Be("Test Grouping");
        result.LightSwatchId.Should().Be("light1");
        result.DarkSwatchId.Should().Be("dark1");
    }

    /// <summary>
    ///     Verifies that the service provides sensible default values for metadata
    ///     when processing an audio file with no tags.
    /// </summary>
    [Fact]
    public async Task ExtractMetadataAsync_WithMinimalMetadata_ProvidesSaneDefaults()
    {
        // Arrange
        var filePath = CreateTestAudioFile("minimal.mp3", track =>
        {
            // Clear any default values
            track.Title = "";
            track.Artist = "";
            track.Album = "";
        });

        // Act
        var result = await _metadataService.ExtractMetadataAsync(filePath);

        // Assert
        result.Should().NotBeNull();
        result.Title.Should().Be("minimal"); // Falls back to filename
        result.Artist.Should().Be("Unknown Artist");
        result.Album.Should().Be("Unknown Album");
        result.AlbumArtist.Should().Be("Unknown Artist");
        result.Year.Should().BeNull();
        result.CoverArtUri.Should().BeNull();
    }



    /// <summary>
    ///     Verifies that if album art processing fails for a given song, the error is handled
    ///     gracefully and a subsequent request will retry the operation.
    /// </summary>
    [Fact]
    public async Task ProcessAlbumArtAsync_WhenProcessingFails_AllowsRetryOnNextCall()
    {
        // Arrange
        var pictureData = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }; // PNG header
        var filePath = CreateTestAudioFile("failsong.mp3", track =>
        {
            track.Album = "Failing Album";
            track.AlbumArtist = "Failing Artist";
            track.EmbeddedPictures.Add(PictureInfo.fromBinaryData(pictureData));
        });

        // First call fails
        _imageProcessor.SaveCoverArtAndExtractColorsAsync(Arg.Any<byte[]>())
            .ThrowsAsync(new InvalidOperationException("Processing failed"));

        // Act (First attempt)
        var result1 = await _metadataService.ExtractMetadataAsync(filePath);

        // Assert (First attempt)
        result1.CoverArtUri.Should().BeNull();
        await _imageProcessor.Received(1)
            .SaveCoverArtAndExtractColorsAsync(Arg.Any<byte[]>());

        // Arrange (Second attempt succeeds)
        _imageProcessor.ClearReceivedCalls();
        _imageProcessor.SaveCoverArtAndExtractColorsAsync(Arg.Any<byte[]>())
            .Returns(Task.FromResult<(string?, string?, string?)>(("C:/art/success.jpg", "lightOK", "darkOK")));

        // Act (Second attempt)
        var result2 = await _metadataService.ExtractMetadataAsync(filePath);

        // Assert (Second attempt)
        await _imageProcessor.Received(1)
            .SaveCoverArtAndExtractColorsAsync(Arg.Any<byte[]>());
        result2.CoverArtUri.Should().Be("C:/art/success.jpg");
    }

    /// <summary>
    ///     Tests that if a valid (newer) cached LRC file exists for an audio file,
    ///     it is used directly without re-extracting lyrics.
    /// </summary>
    [Fact]
    public async Task GetLrcPathAsync_WithValidCache_ReturnsCachedPathWithoutExtraction()
    {
        // Arrange
        var audioFilePath = CreateTestAudioFile("cached.mp3", track =>
        {
            track.Title = "Cached Song";
            track.Artist = "Cache Artist";
        });
        var audioFileTime = new DateTime(2023, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var cacheFileTime = audioFileTime.AddHours(1);

        File.SetLastWriteTimeUtc(audioFilePath, audioFileTime);
        _fileSystem.GetFileInfo(audioFilePath).Returns(new FileInfo(audioFilePath));

        // Use the same helper as the production code to generate the expected cache path
        var expectedCachePath = Path.Combine(LrcCachePath, FileNameHelper.GenerateLrcCacheFileName("Cache Artist", null, "Cached Song"));

        _fileSystem.FileExists(expectedCachePath).Returns(true);
        _fileSystem.GetLastWriteTimeUtc(expectedCachePath).Returns(cacheFileTime);

        // Act
        var result = await _metadataService.ExtractMetadataAsync(audioFilePath);

        // Assert
        result.LrcFilePath.Should().Be(expectedCachePath);
        await _fileSystem.DidNotReceive().WriteAllTextAsync(Arg.Any<string>(), Arg.Any<string>());
    }

    /// <summary>
    ///     Verifies that the service can find an external LRC file located in the same
    ///     directory as the audio file when no embedded or cached lyrics are available.
    /// </summary>
    [Fact]
    public async Task FindLrcFilePath_WithExternalLrcFile_ReturnsExternalPath()
    {
        // Arrange
        var audioFilePath = CreateTestAudioFile("external.mp3", track =>
        {
            /* no lyrics */
        });
        var expectedLrcPath = Path.Combine(_tempDirectory, "external.lrc");

        _fileSystem.FileExists(Arg.Is<string>(s => s.Contains(LrcCachePath))).Returns(false);
        _fileSystem.GetFiles(_tempDirectory, "*.lrc").Returns(new[] { expectedLrcPath });
        _fileSystem.GetFileNameWithoutExtension(expectedLrcPath).Returns("external");

        // Act
        var result = await _metadataService.ExtractMetadataAsync(audioFilePath);

        // Assert
        result.LrcFilePath.Should().Be(expectedLrcPath);
    }

    /// <summary>
    ///     Verifies that the service can find an external TXT lyrics file located in the same
    ///     directory as the audio file when no LRC file is found.
    /// </summary>
    [Fact]
    public async Task FindLrcFilePath_WithExternalTxtFile_ReturnsTxtPath()
    {
        // Arrange
        var audioFilePath = CreateTestAudioFile("externaltxt.mp3", track =>
        {
            /* no lyrics */
        });
        var expectedTxtPath = Path.Combine(_tempDirectory, "externaltxt.txt");

        _fileSystem.FileExists(Arg.Is<string>(s => s.Contains(LrcCachePath))).Returns(false);
        _fileSystem.GetFiles(_tempDirectory, "*.lrc").Returns(Array.Empty<string>()); // No LRC file
        _fileSystem.GetFiles(_tempDirectory, "*.txt").Returns(new[] { expectedTxtPath });
        _fileSystem.GetFileNameWithoutExtension(expectedTxtPath).Returns("externaltxt");

        // Act
        var result = await _metadataService.ExtractMetadataAsync(audioFilePath);

        // Assert
        result.LrcFilePath.Should().Be(expectedTxtPath);
    }

    /// <summary>
    ///     Ensures the service handles corrupt audio files gracefully by either returning a
    ///     failed result or providing sensible fallback values. ATL.NET is more lenient than
    ///     TagLibSharp and may not always detect corrupt files.
    /// </summary>
    [Fact]
    public async Task ExtractMetadataAsync_WithCorruptFile_HandlesGracefully()
    {
        // Arrange - write invalid/corrupt data that looks like an audio file but isn't valid
        var filePath = Path.Combine(_tempDirectory, "corrupt.mp3");
        // Write data that starts with MP3 sync but is too corrupted to parse
        File.WriteAllBytes(filePath, new byte[] { 0xFF, 0xFB, 0x00 });
        _fileSystem.GetFileInfo(filePath).Returns(new FileInfo(filePath));
        _fileSystem.FileExists(filePath).Returns(true);

        // Act
        var result = await _metadataService.ExtractMetadataAsync(filePath);

        // Assert - ATL may report failure OR provide fallback values (both are acceptable)
        result.Should().NotBeNull();
        if (result.ExtractionFailed)
        {
            result.ErrorMessage.Should().BeOneOf("CorruptFile", "UnsupportedFormat");
        }
        else
        {
            // ATL gracefully degraded - should have fallback values
            result.Title.Should().Be("corrupt");
        }
    }

    /// <summary>
    ///     Ensures the service handles unsupported file formats gracefully by either returning a
    ///     failed result or providing sensible fallback values. ATL.NET is more lenient than
    ///     TagLibSharp and may not always detect unsupported formats.
    /// </summary>
    [Fact]
    public async Task ExtractMetadataAsync_WithUnsupportedFormat_HandlesGracefully()
    {
        // Arrange
        var filePath = Path.Combine(_tempDirectory, "document.txt");
        File.WriteAllText(filePath, "this is not a music file");
        _fileSystem.GetFileInfo(filePath).Returns(new FileInfo(filePath));
        _fileSystem.FileExists(filePath).Returns(true);

        // Act
        var result = await _metadataService.ExtractMetadataAsync(filePath);

        // Assert - ATL may report failure OR provide fallback values (both are acceptable)
        result.Should().NotBeNull();
        if (result.ExtractionFailed)
        {
            result.ErrorMessage.Should().BeOneOf("CorruptFile", "UnsupportedFormat");
        }
        else
        {
            // ATL gracefully degraded - should have fallback values
            result.Title.Should().Be("document");
        }
    }

    /// <summary>
    ///     Verifies that when no embedded album art is found, the service falls back to
    ///     searching for cover art files in the directory hierarchy and uses ReadAllBytesAsync.
    /// </summary>
    [Fact]
    public async Task ProcessAlbumArtAsync_WithDirectoryCoverArt_UsesFallback()
    {
        // Arrange
        var audioFilePath = CreateTestAudioFile("nocoverart.mp3", track =>
        {
            track.Title = "No Cover Song";
            track.Artist = "No Cover Artist";
            // No embedded pictures
        });

        var coverArtPath = Path.Combine(_tempDirectory, "cover.jpg");
        var coverArtBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }; // PNG header

        // Mock file system to find cover art in directory
        _fileSystem.GetFiles(_tempDirectory, "*.*").Returns(new[] { coverArtPath });
        _fileSystem.GetExtension(coverArtPath).Returns(".jpg");
        _fileSystem.GetFileNameWithoutExtension(coverArtPath).Returns("cover");
        _fileSystem.ReadAllBytesAsync(coverArtPath).Returns(Task.FromResult(coverArtBytes));

        _imageProcessor.SaveCoverArtAndExtractColorsAsync(Arg.Any<byte[]>())
            .Returns(Task.FromResult<(string?, string?, string?)>(("C:/art/cover.jpg", "lightCover", "darkCover")));

        // Act
        var result = await _metadataService.ExtractMetadataAsync(audioFilePath);

        // Assert
        result.CoverArtUri.Should().Be("C:/art/cover.jpg");
        result.LightSwatchId.Should().Be("lightCover");
        result.DarkSwatchId.Should().Be("darkCover");
        await _fileSystem.Received(1).ReadAllBytesAsync(coverArtPath);
    }

    /// <summary>
    ///     Verifies that when caching embedded synchronized lyrics, the service creates
    ///     the LRC cache directory if it doesn't exist.
    /// </summary>
    [Fact]
    public async Task ExtractAndCacheEmbeddedLrcAsync_CreatesDirectoryIfNotExists()
    {
        // Arrange
        var audioFilePath = CreateTestAudioFile("synced.mp3", track =>
        {
            track.Title = "Synced Song";
            track.Artist = "Synced Artist";
            var lyricsInfo = new LyricsInfo
            {
                SynchronizedLyrics =
                [
                    new LyricsInfo.LyricsPhrase(1000, "First line"),
                    new LyricsInfo.LyricsPhrase(5000, "Second line")
                ]
            };
            track.Lyrics.Add(lyricsInfo);
        });

        var expectedCachePath = Path.Combine(LrcCachePath, FileNameHelper.GenerateLrcCacheFileName("Synced Artist", null, "Synced Song"));

        // Mock: cache file doesn't exist yet
        _fileSystem.FileExists(Arg.Is<string>(s => s.Contains(LrcCachePath))).Returns(false);
        // Mock: cache directory doesn't exist
        _fileSystem.DirectoryExists(LrcCachePath).Returns(false);

        // Act
        var result = await _metadataService.ExtractMetadataAsync(audioFilePath);

        // Assert
        _fileSystem.Received(1).DirectoryExists(LrcCachePath);
        _fileSystem.Received(1).CreateDirectory(LrcCachePath);
        await _fileSystem.Received(1).WriteAllTextAsync(expectedCachePath, Arg.Any<string>());
        result.LrcFilePath.Should().Be(expectedCachePath);
    }

    /// <summary>
    ///     Verifies that ReplayGain tags in various formats are correctly extracted and parsed.
    /// </summary>
    [Fact]
    public async Task ExtractMetadataAsync_WithReplayGainTags_ExtractsCorrectly()
    {
        // Arrange
        var filePath = CreateTestAudioFile("replaygain.mp3", track =>
        {
            track.Title = "ReplayGain Song";
            track.AdditionalFields.Add("REPLAYGAIN_TRACK_GAIN", "-6.54 dB");
            track.AdditionalFields.Add("REPLAYGAIN_TRACK_PEAK", "0.985432");
        });

        // Act
        var result = await _metadataService.ExtractMetadataAsync(filePath);

        // Assert
        result.ReplayGainTrackGain.Should().Be(-6.54);
        result.ReplayGainTrackPeak.Should().Be(0.985432);

        // Test with different suffixes and messy strings
        var filePath2 = CreateTestAudioFile("replaygain2.mp3", track =>
        {
            track.Title = "ReplayGain Messy";
            track.AdditionalFields.Add("REPLAYGAIN_TRACK_GAIN", "+2.5dB (more info)");
            track.AdditionalFields.Add("REPLAYGAIN_TRACK_PEAK", "1.1 peak value");
        });

        var result2 = await _metadataService.ExtractMetadataAsync(filePath2);
        result2.ReplayGainTrackGain.Should().Be(2.5);
        result2.ReplayGainTrackPeak.Should().Be(1.1);
    }
}
