using System.Security.Cryptography;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Nagi.Core.Helpers;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Implementations;
using NSubstitute;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace Nagi.Core.Tests;

public class ImageSharpProcessorTests
{
    private const string AlbumArtPath = "C:\\cache\\art";
    private readonly IFileSystemService _fileSystem;
    private readonly ImageSharpProcessor _imageProcessor;

    public ImageSharpProcessorTests()
    {
        var pathConfig = Substitute.For<IPathConfiguration>();
        _fileSystem = Substitute.For<IFileSystemService>();
        var logger = Substitute.For<ILogger<ImageSharpProcessor>>();

        pathConfig.AlbumArtCachePath.Returns(AlbumArtPath);

        _imageProcessor = new ImageSharpProcessor(pathConfig, _fileSystem, logger);
    }

    [Fact]
    public async Task SaveCoverArtAndExtractColorsAsync_WhenFileDoesNotExist_SavesFileAndExtractsColors()
    {
        var pictureData = CreateTestImageBytes();
        var contentHash = GenerateContentHash(pictureData);
        _fileSystem.GetFiles(AlbumArtPath, $"{contentHash}.*.fetched.jpg").Returns(Array.Empty<string>());

        var (uri, lightSwatch, darkSwatch) =
            await _imageProcessor.SaveCoverArtAndExtractColorsAsync(pictureData);

        uri.Should().NotBeNull();
        uri.Should().StartWith(Path.Combine(AlbumArtPath, contentHash));
        uri.Should().EndWith(".fetched.jpg");
        lightSwatch.Should().NotBeNull();
        darkSwatch.Should().NotBeNull();
        await _fileSystem.Received(1).WriteAllBytesAsync(Arg.Any<string>(), Arg.Any<byte[]>());
        _fileSystem.Received(1).MoveFile(Arg.Any<string>(), Arg.Any<string>(), true);
    }

    [Fact]
    public async Task SaveCoverArtAndExtractColorsAsync_WhenFileAlreadyExists_SkipsSaveAndExtractsColors()
    {
        var pictureData = CreateTestImageBytes();
        var contentHash = GenerateContentHash(pictureData);
        var cachedPath = Path.Combine(AlbumArtPath, $"{contentHash}.abcdef.123456.fetched.jpg");
        _fileSystem.GetFiles(AlbumArtPath, $"{contentHash}.*.fetched.jpg").Returns(new[] { cachedPath });

        var (uri, lightSwatch, darkSwatch) =
            await _imageProcessor.SaveCoverArtAndExtractColorsAsync(pictureData);

        uri.Should().Be(cachedPath);
        lightSwatch.Should().Be("abcdef");
        darkSwatch.Should().Be("123456");
        await _fileSystem.DidNotReceive().WriteAllBytesAsync(Arg.Any<string>(), Arg.Any<byte[]>());
    }

    [Fact]
    public async Task SaveCoverArtAndExtractColorsAsync_WithDistinctLargePngs_SavesEveryConcurrentImage()
    {
        var pictures = Enumerable.Range(0, 4).Select(index =>
            CreateTestImageBytes(width: 4000 + index, height: 4000)).ToArray();

        var results = await Task.WhenAll(pictures.Select(data => Task.Run(() =>
            _imageProcessor.SaveCoverArtAndExtractColorsAsync(data))));

        results.Select(result => result.uri).Should().OnlyContain(uri => uri != null)
            .And.OnlyHaveUniqueItems();
        await _fileSystem.Received(4).WriteAllBytesAsync(Arg.Any<string>(), Arg.Any<byte[]>());
    }

    [Fact]
    public async Task SaveCoverArtAndExtractColorsAsync_WithInvalidImageData_ReturnsNullValues()
    {
        var invalidPictureData = new byte[] { 1, 2, 3, 4 };
        var contentHash = GenerateContentHash(invalidPictureData);
        _fileSystem.GetFiles(AlbumArtPath, $"{contentHash}.*.fetched.jpg").Returns(Array.Empty<string>());

        var (uri, lightSwatch, darkSwatch) =
            await _imageProcessor.SaveCoverArtAndExtractColorsAsync(invalidPictureData);

        uri.Should().BeNull();
        lightSwatch.Should().BeNull();
        darkSwatch.Should().BeNull();
    }

    [Fact]
    public async Task SaveCoverArtAndExtractColorsAsync_WhenMoveFails_DoesNotReturnMissingImageAndCanRetry()
    {
        var pictureData = CreateTestImageBytes();
        _fileSystem.FileExists(Arg.Is<string>(path => path.EndsWith(".tmp"))).Returns(true);
        var failMove = true;
        _fileSystem.When(fs => fs.MoveFile(Arg.Any<string>(), Arg.Any<string>(), true))
            .Do(_ =>
            {
                if (failMove) throw new IOException("Destination unavailable");
            });

        var failed = await _imageProcessor.SaveCoverArtAndExtractColorsAsync(pictureData);

        failed.Should().Be((null, null, null));
        _fileSystem.Received(1).DeleteFile(Arg.Is<string>(path => path.EndsWith(".tmp")));

        failMove = false;
        var retried = await _imageProcessor.SaveCoverArtAndExtractColorsAsync(pictureData);

        retried.uri.Should().NotBeNull();
        _fileSystem.Received(2).MoveFile(Arg.Any<string>(), Arg.Any<string>(), true);
    }

    [Fact]
    public async Task SaveCoverArtAndExtractColorsAsync_PreservesRedAndBlueChannels()
    {
        var pictureData = CreateTestImageBytes(20, 10);
        byte[]? savedImage = null;
        _fileSystem.WriteAllBytesAsync(Arg.Any<string>(), Arg.Any<byte[]>())
            .Returns(call =>
            {
                savedImage = call.ArgAt<byte[]>(1);
                return Task.CompletedTask;
            });

        var (_, light, dark) = await _imageProcessor.SaveCoverArtAndExtractColorsAsync(pictureData);

        using var image = Image.Load<Rgba32>(savedImage!);
        image[0, 0].R.Should().BeGreaterThan(240);
        image[0, 0].B.Should().BeLessThan(15);
        var palette = MaterialColorUtilities.Palettes.CorePalette.ContentOf(0xFFFF0000);
        light.Should().Be((new MaterialColorUtilities.Schemes.LightSchemeMapper().Map(palette).Primary & 0x00FFFFFF).ToString("x6"));
        dark.Should().Be((new MaterialColorUtilities.Schemes.DarkSchemeMapper().Map(palette).Primary & 0x00FFFFFF).ToString("x6"));
    }

    [Fact]
    public async Task SaveCoverArtAndExtractColorsAsync_WithEmptyData_ReturnsNullValues()
    {
        var emptyData = Array.Empty<byte>();

        var (uri, lightSwatch, darkSwatch) =
            await _imageProcessor.SaveCoverArtAndExtractColorsAsync(emptyData);

        uri.Should().BeNull();
        lightSwatch.Should().BeNull();
        darkSwatch.Should().BeNull();
        await _fileSystem.DidNotReceive().WriteAllBytesAsync(Arg.Any<string>(), Arg.Any<byte[]>());
    }

    [Fact]
    public async Task ProcessImageBytesAsync_WhenImageIsWithinBounds_ReturnsOriginalBytes()
    {
        var imageData = CreateTestImageBytes();

        var result = await _imageProcessor.ProcessImageBytesAsync(imageData);

        result.Should().Equal(imageData);
    }

    [Fact]
    public async Task ProcessImageBytesAsync_WhenImageExceedsBounds_ResizesToMaxDimension()
    {
        var imageData = CreateTestImageBytes(width: 1200, height: 600);

        var result = await _imageProcessor.ProcessImageBytesAsync(imageData, maxDimension: 300);

        result.Should().NotEqual(imageData);
        using var image = Image.Load<Rgba32>(result);
        image.Width.Should().Be(300);
        image.Height.Should().Be(150);
    }

    private static byte[] CreateTestImageBytes()
    {
        return Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");
    }

    private static byte[] CreateTestImageBytes(int width, int height)
    {
        using var image = new Image<Rgba32>(width, height, new Rgba32(255, 0, 0));
        using var stream = new MemoryStream();
        image.SaveAsPng(stream);
        return stream.ToArray();
    }

    private static string GenerateContentHash(byte[] pictureData)
    {
        var hashBytes = SHA256.HashData(pictureData);
        return Convert.ToHexStringLower(hashBytes);
    }

}
