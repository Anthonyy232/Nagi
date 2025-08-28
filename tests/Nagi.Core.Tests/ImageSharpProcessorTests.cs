﻿using System.Security.Cryptography;
using System.Text;
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
///     These tests verify the service's ability to save cover art, handle existing files,
///     extract color swatches, and manage errors related to image processing and file I/O.
/// </summary>
public class ImageSharpProcessorTests
{
    private const string AlbumArtPath = "C:\\cache\\art";
    private const string ArtistName = "Test Artist";
    private const string AlbumTitle = "Test Album";
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
        var stableId = GenerateStableId(ArtistName, AlbumTitle);
        var expectedPath = Path.Combine(AlbumArtPath, $"{stableId}.jpg");
        _fileSystem.FileExists(expectedPath).Returns(false);

        // Act
        var (uri, lightSwatch, darkSwatch) =
            await _imageProcessor.SaveCoverArtAndExtractColorsAsync(pictureData, AlbumTitle, ArtistName);

        // Assert
        uri.Should().Be(expectedPath);
        lightSwatch.Should().NotBeNull();
        darkSwatch.Should().NotBeNull();
        await _fileSystem.Received(1).WriteAllBytesAsync(expectedPath, pictureData);
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
        var stableId = GenerateStableId(ArtistName, AlbumTitle);
        var expectedPath = Path.Combine(AlbumArtPath, $"{stableId}.jpg");
        _fileSystem.FileExists(expectedPath).Returns(true);

        // Act
        var (uri, lightSwatch, darkSwatch) =
            await _imageProcessor.SaveCoverArtAndExtractColorsAsync(pictureData, AlbumTitle, ArtistName);

        // Assert
        uri.Should().Be(expectedPath);
        lightSwatch.Should().NotBeNull();
        darkSwatch.Should().NotBeNull();
        await _fileSystem.DidNotReceive().WriteAllBytesAsync(Arg.Any<string>(), Arg.Any<byte[]>());
    }

    /// <summary>
    ///     Verifies that processing invalid or corrupt image data results in null color swatches
    ///     but still returns a valid file path URI and attempts to save the file.
    /// </summary>
    [Fact]
    public async Task SaveCoverArtAndExtractColorsAsync_WithInvalidImageData_ReturnsUriWithNullColors()
    {
        // Arrange
        var invalidPictureData = new byte[] { 1, 2, 3, 4 };
        var stableId = GenerateStableId(ArtistName, AlbumTitle);
        var expectedPath = Path.Combine(AlbumArtPath, $"{stableId}.jpg");
        _fileSystem.FileExists(expectedPath).Returns(false);

        // Act
        var (uri, lightSwatch, darkSwatch) =
            await _imageProcessor.SaveCoverArtAndExtractColorsAsync(invalidPictureData, AlbumTitle, ArtistName);

        // Assert
        uri.Should().Be(expectedPath);
        lightSwatch.Should().BeNull();
        darkSwatch.Should().BeNull();
        await _fileSystem.Received(1).WriteAllBytesAsync(expectedPath, invalidPictureData);
    }

    /// <summary>
    ///     Verifies that an <see cref="IOException" /> during the file writing process is correctly
    ///     propagated up to the caller.
    /// </summary>
    [Fact]
    public async Task SaveCoverArtAndExtractColorsAsync_WhenFileWriteFails_PropagatesException()
    {
        // Arrange
        var pictureData = CreateTestImageBytes();
        var stableId = GenerateStableId(ArtistName, AlbumTitle);
        var expectedPath = Path.Combine(AlbumArtPath, $"{stableId}.jpg");
        _fileSystem.FileExists(expectedPath).Returns(false);
        _fileSystem.WriteAllBytesAsync(expectedPath, pictureData).ThrowsAsync(new IOException("Disk full"));

        // Act & Assert
        await _imageProcessor
            .Awaiting(p => p.SaveCoverArtAndExtractColorsAsync(pictureData, AlbumTitle, ArtistName))
            .Should().ThrowAsync<IOException>().WithMessage("Disk full");
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
    ///     Generates a stable, deterministic ID based on artist and album names for consistent file naming.
    /// </summary>
    private static string GenerateStableId(string artistName, string albumTitle)
    {
        using var sha = SHA256.Create();
        var textToHash = $"{artistName}_{albumTitle}";
        var hashBytes = sha.ComputeHash(Encoding.UTF8.GetBytes(textToHash));
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    #endregion
}