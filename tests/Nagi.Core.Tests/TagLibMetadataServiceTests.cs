using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Nagi.Core.Helpers;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Implementations;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using TagLib;
using Xunit;
using File = TagLib.File; // Alias to avoid conflict with System.IO.File

namespace Nagi.Core.Tests;

/// <summary>
///     Contains unit tests for the <see cref="TagLibMetadataService" />.
///     These tests verify the service's ability to extract metadata, handle album art,
///     manage lyrics, and gracefully handle various file-related errors.
///     The tests utilize a real temporary file system for TagLib-Sharp interactions,
///     while all other external dependencies like image processing and file system access
///     are mocked using NSubstitute.
/// </summary>
public class TagLibMetadataServiceTests : IDisposable
{
    private const string LrcCachePath = "C:\\cache\\lrc";
    private readonly IFileSystemService _fileSystem;
    private readonly IImageProcessor _imageProcessor;
    private readonly ILogger<TagLibMetadataService> _logger;
    private readonly TagLibMetadataService _metadataService;
    private readonly IPathConfiguration _pathConfig;
    private readonly string _tempDirectory;

    public TagLibMetadataServiceTests()
    {
        _imageProcessor = Substitute.For<IImageProcessor>();
        _fileSystem = Substitute.For<IFileSystemService>();
        _pathConfig = Substitute.For<IPathConfiguration>();
        _logger = Substitute.For<ILogger<TagLibMetadataService>>();

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

        _metadataService = new TagLibMetadataService(_imageProcessor, _fileSystem, _pathConfig, _logger);
    }

    public void Dispose()
    {
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
    /// <param name="tagFileSetup">An action to configure the tags on the created <see cref="File" /> object.</param>
    /// <returns>The full path to the newly created temporary audio file.</returns>
    /// <remarks>
    ///     This method writes a minimal valid MP3 frame to the file to prevent TagLib-Sharp from
    ///     throwing a "corrupt file" exception. It also configures the mocked <see cref="IFileSystemService" />
    ///     to recognize the existence of this new file.
    /// </remarks>
    private string CreateTestAudioFile(string fileName, Action<File> tagFileSetup)
    {
        var filePath = Path.Combine(_tempDirectory, fileName);
        var minimalMp3Content = new byte[] { 0xFF, 0xFB, 0x90, 0x64, 0x00, 0x00, 0x00, 0x00 };
        System.IO.File.WriteAllBytes(filePath, minimalMp3Content);

        using (var tagFile = File.Create(filePath))
        {
            tagFileSetup(tagFile);
            tagFile.Save();
        }

        _fileSystem.GetFileInfo(filePath).Returns(new FileInfo(filePath));
        _fileSystem.FileExists(filePath).Returns(true);

        return filePath;
    }

    /// <summary>
    ///     Verifies that the service correctly extracts all standard metadata properties
    ///     from a well-tagged audio file.
    /// </summary>
    /// <remarks>
    ///     <b>Scenario:</b> An MP3 file is provided with a comprehensive set of tags,
    ///     including title, artist, album, year, track/disc numbers, BPM, genres, lyrics, and embedded cover art.
    ///     The image processor is mocked to successfully process the cover art.
    ///     <br />
    ///     <b>Expected Result:</b> The returned <see cref="TagLib.Flac.Metadata" /> object should contain all the
    ///     corresponding values from the file's tags, and the album art processing results
    ///     (URI and color swatches) should be correctly populated.
    /// </remarks>
    [Fact]
    public async Task ExtractMetadataAsync_WithFullMetadata_ExtractsAllPropertiesCorrectly()
    {
        // Arrange
        var pictureData = new byte[] { 1, 2, 3 };
        var filePath = CreateTestAudioFile("full.mp3", tagFile =>
        {
            var tag = tagFile.Tag;
            tag.Title = "Test Title";
            tag.Performers = new[] { "Test Artist" };
            tag.AlbumArtists = new[] { "Test Album Artist" };
            tag.Album = "Test Album";
            tag.Year = 2023;
            tag.Track = 5;
            tag.TrackCount = 10;
            tag.Disc = 1;
            tag.DiscCount = 2;
            tag.BeatsPerMinute = 120;
            tag.Lyrics = "Some simple lyrics";
            tag.Genres = new[] { "Rock", "Pop" };
            tag.Pictures = new IPicture[] { new Picture(pictureData) };
        });

        _imageProcessor.SaveCoverArtAndExtractColorsAsync(Arg.Any<byte[]>(), filePath)
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
        result.LightSwatchId.Should().Be("light1");
        result.DarkSwatchId.Should().Be("dark1");
    }

    /// <summary>
    ///     Verifies that the service provides sensible default values for metadata
    ///     when processing an audio file with no tags.
    /// </summary>
    /// <remarks>
    ///     <b>Scenario:</b> An audio file is provided that contains no embedded metadata tags.
    ///     <br />
    ///     <b>Expected Result:</b> The service should not fail. It should return a <see cref="TagLib.Flac.Metadata" />
    ///     object where the title falls back to the file name, artist and album fall back to
    ///     "Unknown" placeholders, and nullable properties are correctly set to null.
    /// </remarks>
    [Fact]
    public async Task ExtractMetadataAsync_WithMinimalMetadata_ProvidesSaneDefaults()
    {
        // Arrange
        var filePath = CreateTestAudioFile("minimal.mp3", tagFile =>
        {
            /* No tags set */
        });

        // Act
        var result = await _metadataService.ExtractMetadataAsync(filePath);

        // Assert
        result.Should().NotBeNull();
        result.Title.Should().Be("minimal");
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
    /// <remarks>
    ///     <b>Scenario:</b> An audio file is processed, and the mocked image processor throws an
    ///     exception. The same file is then processed a second time, but the mock is now
    ///     configured to succeed.
    ///     <br />
    ///     <b>Expected Result:</b> The first attempt should result in null album art info. The
    ///     image processor should be called again on the second attempt, which should then succeed,
    ///     populating the album art info correctly.
    /// </remarks>
    [Fact]
    public async Task ProcessAlbumArtAsync_WhenProcessingFails_AllowsRetryOnNextCall()
    {
        // Arrange
        var pictureData = new byte[] { 1, 2, 3 };
        var filePath = CreateTestAudioFile("failsong.mp3", tagFile =>
        {
            var tag = tagFile.Tag;
            tag.Album = "Failing Album";
            tag.AlbumArtists = new[] { "Failing Artist" };
            tag.Pictures = new IPicture[] { new Picture(pictureData) };
        });

        // First call fails
        _imageProcessor.SaveCoverArtAndExtractColorsAsync(Arg.Any<byte[]>(), filePath)
            .ThrowsAsync(new InvalidOperationException("Processing failed"));

        // Act (First attempt)
        var result1 = await _metadataService.ExtractMetadataAsync(filePath);

        // Assert (First attempt)
        result1.CoverArtUri.Should().BeNull();
        await _imageProcessor.Received(1)
            .SaveCoverArtAndExtractColorsAsync(Arg.Any<byte[]>(), filePath);

        // Arrange (Second attempt succeeds)
        _imageProcessor.ClearReceivedCalls();
        _imageProcessor.SaveCoverArtAndExtractColorsAsync(Arg.Any<byte[]>(), filePath)
            .Returns(Task.FromResult<(string?, string?, string?)>(("C:/art/success.jpg", "lightOK", "darkOK")));

        // Act (Second attempt)
        var result2 = await _metadataService.ExtractMetadataAsync(filePath);

        // Assert (Second attempt)
        await _imageProcessor.Received(1)
            .SaveCoverArtAndExtractColorsAsync(Arg.Any<byte[]>(), filePath);
        result2.CoverArtUri.Should().Be("C:/art/success.jpg");
    }

    /// <summary>
    ///     Tests that if a valid (newer) cached LRC file exists for an audio file,
    ///     it is used directly without re-extracting lyrics.
    /// </summary>
    /// <remarks>
    ///     <b>Scenario:</b> An audio file is processed. The file system is mocked to report that
    ///     a corresponding LRC file exists in the cache directory and its last write time is
    ///     more recent than the audio file's last write time.
    ///     <br />
    ///     <b>Expected Result:</b> The returned <see cref="TagLib.Flac.Metadata" /> object's `LrcFilePath`
    ///     should point to the existing cache file, and no attempt should be made to write a
    ///     new file to the cache.
    /// </remarks>
    [Fact]
    public async Task GetLrcPathAsync_WithValidCache_ReturnsCachedPathWithoutExtraction()
    {
        // Arrange
        var audioFilePath = CreateTestAudioFile("cached.mp3", tagFile =>
        {
            /* no lyrics needed */
        });
        var audioFileTime = new DateTime(2023, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var cacheFileTime = audioFileTime.AddHours(1);

        System.IO.File.SetLastWriteTimeUtc(audioFilePath, audioFileTime);
        _fileSystem.GetFileInfo(audioFilePath).Returns(new FileInfo(audioFilePath));

        string cacheKey;
        using (var sha256 = SHA256.Create())
        {
            var hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(audioFilePath));
            cacheKey = Convert.ToBase64String(hashBytes).Replace('/', '_').Replace('+', '-');
        }

        var expectedCachePath = Path.Combine(LrcCachePath, $"{cacheKey}.lrc");

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
    /// <remarks>
    ///     <b>Scenario:</b> An audio file with no embedded lyrics is processed. The file system
    ///     is mocked to report that no cached LRC file exists, but a file with the same base name
    ///     and a ".lrc" extension exists in the same directory.
    ///     <br />
    ///     <b>Expected Result:</b> The returned `LrcFilePath` should be the path to the
    ///     external ".lrc" file.
    /// </remarks>
    [Fact]
    public async Task FindLrcFilePath_WithExternalLrcFile_ReturnsExternalPath()
    {
        // Arrange
        var audioFilePath = CreateTestAudioFile("external.mp3", tagFile =>
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
    ///     Ensures the service handles corrupt audio files gracefully by returning a
    ///     failed result with an appropriate error message.
    /// </summary>
    /// <remarks>
    ///     <b>Scenario:</b> The service is asked to process a zero-byte file, which
    ///     TagLib-Sharp considers to be a corrupt file.
    ///     <br />
    ///     <b>Expected Result:</b> The returned <see cref="TagLib.Flac.Metadata" /> object should have
    ///     `ExtractionFailed` set to true and an `ErrorMessage` of "CorruptFile".
    /// </remarks>
    [Fact]
    public async Task ExtractMetadataAsync_WithCorruptFile_ReturnsFailedResult()
    {
        // Arrange
        var filePath = Path.Combine(_tempDirectory, "corrupt.mp3");
        System.IO.File.WriteAllBytes(filePath, Array.Empty<byte>());
        _fileSystem.GetFileInfo(filePath).Returns(new FileInfo(filePath));
        _fileSystem.FileExists(filePath).Returns(true);

        // Act
        var result = await _metadataService.ExtractMetadataAsync(filePath);

        // Assert
        result.ExtractionFailed.Should().BeTrue();
        result.ErrorMessage.Should().Be("CorruptFile");
    }

    /// <summary>
    ///     Ensures the service handles unsupported file formats gracefully by returning a
    ///     failed result with an appropriate error message.
    /// </summary>
    /// <remarks>
    ///     <b>Scenario:</b> The service is asked to process a plain text file, which is not a
    ///     supported audio format.
    ///     <br />
    ///     <b>Expected Result:</b> The returned <see cref="TagLib.Flac.Metadata" /> object should have
    ///     `ExtractionFailed` set to true and an `ErrorMessage` of "UnsupportedFormat".
    /// </remarks>
    [Fact]
    public async Task ExtractMetadataAsync_WithUnsupportedFormat_ReturnsFailedResult()
    {
        // Arrange
        var filePath = Path.Combine(_tempDirectory, "document.txt");
        System.IO.File.WriteAllText(filePath, "this is not a music file");
        _fileSystem.GetFileInfo(filePath).Returns(new FileInfo(filePath));
        _fileSystem.FileExists(filePath).Returns(true);

        // Act
        var result = await _metadataService.ExtractMetadataAsync(filePath);

        // Assert
        result.ExtractionFailed.Should().BeTrue();
        result.ErrorMessage.Should().Be("UnsupportedFormat");
    }
}