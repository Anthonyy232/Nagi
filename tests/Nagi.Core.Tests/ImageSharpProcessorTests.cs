using System.Security.Cryptography;
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
///     Contains unit tests for the <see cref="ImageSharpProcessor" />.
///     These tests verify the service's ability to save cover art with content-based deduplication,
///     handle existing files, extract color swatches, and manage errors related to image processing and file I/O.
/// </summary>
public class ImageSharpProcessorTests
{
    private const string AlbumArtPath = "C:\\cache\\art";
    private readonly IFileSystemService _fileSystem;
    private readonly ImageSharpProcessor _imageProcessor;
    private readonly ILogger<ImageSharpProcessor> _logger;
    private readonly IPathConfiguration _pathConfig;

    public ImageSharpProcessorTests()
    {
        _pathConfig = Substitute.For<IPathConfiguration>();
        _fileSystem = Substitute.For<IFileSystemService>();
        _logger = Substitute.For<ILogger<ImageSharpProcessor>>();

        _pathConfig.AlbumArtCachePath.Returns(AlbumArtPath);

        _fileSystem.Combine(Arg.Any<string>(), Arg.Any<string>())
            .Returns(callInfo => Path.Combine(callInfo.Arg<string[]>()));

        _imageProcessor = new ImageSharpProcessor(_pathConfig, _fileSystem, _logger);
    }

    /// <summary>
    ///     Verifies that the <see cref="ImageSharpProcessor" /> constructor ensures the album art
    ///     cache directory exists upon initialization.
    /// </summary>
    [Fact]
    public void Constructor_WhenCalled_CreatesAlbumArtDirectory()
    {
        // Assert
        _fileSystem.Received(1).CreateDirectory(AlbumArtPath);
    }

    /// <summary>
    ///     Verifies that when a cover art file does not already exist, it is saved to disk and
    ///     color swatches are successfully extracted from the image data.
    /// </summary>
    [Fact]
    public async Task SaveCoverArtAndExtractColorsAsync_WhenFileDoesNotExist_SavesFileAndExtractsColors()
    {
        // Arrange
        var pictureData = CreateTestImageBytes();
        var contentHash = GenerateContentHash(pictureData);
        var expectedPath = Path.Combine(AlbumArtPath, $"{contentHash}.fetched.jpg");
        var expectedTempPath = expectedPath + ".tmp";
        _fileSystem.FileExists(expectedPath).Returns(false);

        // Act
        var (uri, lightSwatch, darkSwatch) =
            await _imageProcessor.SaveCoverArtAndExtractColorsAsync(pictureData);

        // Assert
        uri.Should().Be(expectedPath);
        lightSwatch.Should().NotBeNull();
        darkSwatch.Should().NotBeNull();
        // Verify atomic write pattern: write to temp, then move to final path
        await _fileSystem.Received(1).WriteAllBytesAsync(expectedTempPath, Arg.Any<byte[]>());
        _fileSystem.Received(1).MoveFile(expectedTempPath, expectedPath, false);
    }

    /// <summary>
    ///     Verifies that if a cover art file already exists, the save operation is skipped, but
    ///     color swatches are still extracted from the image data.
    /// </summary>
    [Fact]
    public async Task SaveCoverArtAndExtractColorsAsync_WhenFileAlreadyExists_SkipsSaveAndExtractsColors()
    {
        // Arrange
        var pictureData = CreateTestImageBytes();
        var contentHash = GenerateContentHash(pictureData);
        var expectedPath = Path.Combine(AlbumArtPath, $"{contentHash}.fetched.jpg");
        _fileSystem.FileExists(expectedPath).Returns(true);

        // Act
        var (uri, lightSwatch, darkSwatch) =
            await _imageProcessor.SaveCoverArtAndExtractColorsAsync(pictureData);

        // Assert
        uri.Should().Be(expectedPath);
        lightSwatch.Should().NotBeNull();
        darkSwatch.Should().NotBeNull();
        await _fileSystem.DidNotReceive().WriteAllBytesAsync(Arg.Any<string>(), Arg.Any<byte[]>());
    }

    /// <summary>
    ///     Verifies that identical image data produces the same cache file (deduplication).
    /// </summary>
    [Fact]
    public async Task SaveCoverArtAndExtractColorsAsync_WithIdenticalData_UsesSameCacheFile()
    {
        // Arrange
        var pictureData = CreateTestImageBytes();
        var contentHash = GenerateContentHash(pictureData);
        var expectedPath = Path.Combine(AlbumArtPath, $"{contentHash}.fetched.jpg");
        var expectedTempPath = expectedPath + ".tmp";
        
        // First call - file doesn't exist (needs false for both outer check AND inner double-check)
        // Second call - file exists
        _fileSystem.FileExists(expectedPath).Returns(false, false, true);

        // Act - call twice with same data
        var (uri1, _, _) = await _imageProcessor.SaveCoverArtAndExtractColorsAsync(pictureData);
        var (uri2, _, _) = await _imageProcessor.SaveCoverArtAndExtractColorsAsync(pictureData);

        // Assert - both calls should return the same path
        uri1.Should().Be(expectedPath);
        uri2.Should().Be(expectedPath);
        // File should only be written once (to temp path, then moved)
        await _fileSystem.Received(1).WriteAllBytesAsync(expectedTempPath, Arg.Any<byte[]>());
        _fileSystem.Received(1).MoveFile(expectedTempPath, expectedPath, false);
    }

    /// <summary>
    ///     Verifies that processing invalid or corrupt image data returns null values gracefully.
    /// </summary>
    [Fact]
    public async Task SaveCoverArtAndExtractColorsAsync_WithInvalidImageData_ReturnsNullValues()
    {
        // Arrange
        var invalidPictureData = new byte[] { 1, 2, 3, 4 };
        var contentHash = GenerateContentHash(invalidPictureData);
        var expectedPath = Path.Combine(AlbumArtPath, $"{contentHash}.fetched.jpg");
        _fileSystem.FileExists(expectedPath).Returns(false);

        // Act
        var (uri, lightSwatch, darkSwatch) =
            await _imageProcessor.SaveCoverArtAndExtractColorsAsync(invalidPictureData);

        // Assert - invalid image data causes the whole operation to fail gracefully
        uri.Should().BeNull();
        lightSwatch.Should().BeNull();
        darkSwatch.Should().BeNull();
    }

    /// <summary>
    ///     Verifies that an <see cref="IOException" /> during the file writing process is
    ///     handled gracefully and returns null values.
    /// </summary>
    [Fact]
    public async Task SaveCoverArtAndExtractColorsAsync_WhenFileWriteFails_ReturnsNullValues()
    {
        // Arrange
        var pictureData = CreateTestImageBytes();
        var contentHash = GenerateContentHash(pictureData);
        var expectedPath = Path.Combine(AlbumArtPath, $"{contentHash}.fetched.jpg");
        var expectedTempPath = expectedPath + ".tmp";
        _fileSystem.FileExists(expectedPath).Returns(false);
        // Throw on temp file write to simulate disk full
        _fileSystem.WriteAllBytesAsync(expectedTempPath, Arg.Any<byte[]>()).ThrowsAsync(new IOException("Disk full"));

        // Act
        var (uri, lightSwatch, darkSwatch) =
            await _imageProcessor.SaveCoverArtAndExtractColorsAsync(pictureData);

        // Assert - IO errors are handled gracefully
        uri.Should().BeNull();
        lightSwatch.Should().BeNull();
        darkSwatch.Should().BeNull();
    }

    /// <summary>
    ///     Verifies that empty picture data returns null values without attempting to save.
    /// </summary>
    [Fact]
    public async Task SaveCoverArtAndExtractColorsAsync_WithEmptyData_ReturnsNullValues()
    {
        // Arrange
        var emptyData = Array.Empty<byte>();

        // Act
        var (uri, lightSwatch, darkSwatch) =
            await _imageProcessor.SaveCoverArtAndExtractColorsAsync(emptyData);

        // Assert
        uri.Should().BeNull();
        lightSwatch.Should().BeNull();
        darkSwatch.Should().BeNull();
        await _fileSystem.DidNotReceive().WriteAllBytesAsync(Arg.Any<string>(), Arg.Any<byte[]>());
    }

    #region Helper Methods

    /// <summary>
    ///     Creates a byte array representing a minimal, valid 1x1 red pixel PNG image.
    /// </summary>
    private static byte[] CreateTestImageBytes()
    {
        return Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");
    }

    /// <summary>
    ///     Generates a content-based hash from image data (matches the implementation).
    /// </summary>
    private static string GenerateContentHash(byte[] pictureData)
    {
        var hashBytes = SHA256.HashData(pictureData);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    #endregion
}