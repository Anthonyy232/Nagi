using FluentAssertions;
using Nagi.Core.Helpers;
using Nagi.Core.Services.Abstractions;
using NSubstitute;
using Xunit;

namespace Nagi.Core.Tests;

public class ImageStorageHelperTests
{
    private readonly IFileSystemService _fs;

    public ImageStorageHelperTests()
    {
        _fs = Substitute.For<IFileSystemService>();

        // Default: directory and files don't exist
        _fs.DirectoryExists(Arg.Any<string>()).Returns(false);
        _fs.FileExists(Arg.Any<string>()).Returns(false);

        // Combine delegates to real Path.Combine
        _fs.Combine(Arg.Any<string[]>())
            .Returns(ci => Path.Combine(ci.ArgAt<string[]>(0)));
    }

    // -------------------------------------------------------------------------
    // FindImage
    // -------------------------------------------------------------------------

    [Fact]
    public void FindImage_WhenDirectoryDoesNotExist_ReturnsNull()
    {
        _fs.DirectoryExists("C:\\images").Returns(false);

        var result = ImageStorageHelper.FindImage(_fs, "C:\\images", "abc", ".custom");

        result.Should().BeNull();
    }

    [Fact]
    public void FindImage_WhenNoMatchingFileExists_ReturnsNull()
    {
        _fs.DirectoryExists("C:\\images").Returns(true);
        // No files exist (default)

        var result = ImageStorageHelper.FindImage(_fs, "C:\\images", "abc", ".custom");

        result.Should().BeNull();
    }

    [Fact]
    public void FindImage_WhenMatchingFileExists_ReturnsFirstMatchingPath()
    {
        var dir = "C:\\images";
        var jpgPath = Path.Combine(dir, "abc.custom.jpg");

        _fs.DirectoryExists(dir).Returns(true);
        _fs.FileExists(jpgPath).Returns(true);

        var result = ImageStorageHelper.FindImage(_fs, dir, "abc", ".custom");

        result.Should().Be(jpgPath);
    }

    // -------------------------------------------------------------------------
    // SaveImage
    // -------------------------------------------------------------------------

    [Fact]
    public void SaveImage_WhenSourceFileDoesNotExist_DoesNothing()
    {
        _fs.FileExists("C:\\source.jpg").Returns(false);

        ImageStorageHelper.SaveImage(_fs, "C:\\images", "abc", ".custom", "C:\\source.jpg");

        _fs.DidNotReceive().CopyFile(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>());
    }

    [Fact]
    public void SaveImage_WhenDirectoryDoesNotExist_CreatesDirectory()
    {
        var dir = "C:\\images";
        _fs.FileExists("C:\\source.jpg").Returns(true);
        _fs.DirectoryExists(dir).Returns(false);
        _fs.GetExtension("C:\\source.jpg").Returns(".jpg");

        ImageStorageHelper.SaveImage(_fs, dir, "abc", ".custom", "C:\\source.jpg");

        _fs.Received(1).CreateDirectory(dir);
    }

    [Fact]
    public void SaveImage_CopiesFileWithCorrectDestinationPath()
    {
        var dir = "C:\\images";
        var expected = Path.Combine(dir, "abc.custom.jpg");

        _fs.FileExists("C:\\source.jpg").Returns(true);
        _fs.DirectoryExists(dir).Returns(true);
        _fs.GetExtension("C:\\source.jpg").Returns(".jpg");

        ImageStorageHelper.SaveImage(_fs, dir, "abc", ".custom", "C:\\source.jpg");

        _fs.Received(1).CopyFile("C:\\source.jpg", expected, true);
    }

    [Fact]
    public void SaveImage_DeletesExistingVariantsBeforeCopying()
    {
        var dir = "C:\\images";
        var existingPng = Path.Combine(dir, "abc.custom.png");

        _fs.FileExists("C:\\source.jpg").Returns(true);
        _fs.DirectoryExists(dir).Returns(true);
        _fs.GetExtension("C:\\source.jpg").Returns(".jpg");
        // Simulate an existing .png variant
        _fs.FileExists(existingPng).Returns(true);

        ImageStorageHelper.SaveImage(_fs, dir, "abc", ".custom", "C:\\source.jpg");

        _fs.Received(1).DeleteFile(existingPng);
    }

    // -------------------------------------------------------------------------
    // DeleteImage
    // -------------------------------------------------------------------------

    [Fact]
    public void DeleteImage_WhenDirectoryDoesNotExist_DoesNothing()
    {
        _fs.DirectoryExists("C:\\images").Returns(false);

        ImageStorageHelper.DeleteImage(_fs, "C:\\images", "abc", ".custom");

        _fs.DidNotReceive().DeleteFile(Arg.Any<string>());
    }

    [Fact]
    public void DeleteImage_DeletesAllExistingVariants()
    {
        var dir = "C:\\images";
        var jpgPath = Path.Combine(dir, "abc.custom.jpg");
        var pngPath = Path.Combine(dir, "abc.custom.png");

        _fs.DirectoryExists(dir).Returns(true);
        _fs.FileExists(jpgPath).Returns(true);
        _fs.FileExists(pngPath).Returns(true);

        ImageStorageHelper.DeleteImage(_fs, dir, "abc", ".custom");

        _fs.Received(1).DeleteFile(jpgPath);
        _fs.Received(1).DeleteFile(pngPath);
    }

    [Fact]
    public void DeleteImage_WhenNoVariantsExist_DoesNotCallDeleteFile()
    {
        _fs.DirectoryExists("C:\\images").Returns(true);
        // All _fs.FileExists return false by default

        ImageStorageHelper.DeleteImage(_fs, "C:\\images", "abc", ".custom");

        _fs.DidNotReceive().DeleteFile(Arg.Any<string>());
    }

    // -------------------------------------------------------------------------
    // SaveImageBytesAsync
    // -------------------------------------------------------------------------

    [Fact]
    public async Task SaveImageBytesAsync_WithEmptyBytes_DoesNotWriteFile()
    {
        await ImageStorageHelper.SaveImageBytesAsync(_fs, "C:\\images", "abc", ".custom", []);

        await _fs.DidNotReceive().WriteAllBytesAsync(Arg.Any<string>(), Arg.Any<byte[]>());
    }

    [Fact]
    public async Task SaveImageBytesAsync_WhenDirectoryDoesNotExist_CreatesDirectory()
    {
        var dir = "C:\\images";
        _fs.DirectoryExists(dir).Returns(false);

        await ImageStorageHelper.SaveImageBytesAsync(_fs, dir, "abc", ".custom", [0x01, 0x02]);

        _fs.Received(1).CreateDirectory(dir);
    }

    [Fact]
    public async Task SaveImageBytesAsync_WritesFileWithCorrectPath()
    {
        var dir = "C:\\images";
        var expectedPath = Path.Combine(dir, "abc.custom.jpg");
        var bytes = new byte[] { 0xFF, 0xD8 };

        _fs.DirectoryExists(dir).Returns(true);

        await ImageStorageHelper.SaveImageBytesAsync(_fs, dir, "abc", ".custom", bytes);

        await _fs.Received(1).WriteAllBytesAsync(expectedPath, bytes);
    }

    [Fact]
    public async Task SaveImageBytesAsync_UsesCustomExtension()
    {
        var dir = "C:\\images";
        var expectedPath = Path.Combine(dir, "abc.custom.png");

        _fs.DirectoryExists(dir).Returns(true);

        await ImageStorageHelper.SaveImageBytesAsync(_fs, dir, "abc", ".custom", [0x89, 0x50], ".png");

        await _fs.Received(1).WriteAllBytesAsync(expectedPath, Arg.Any<byte[]>());
    }
}
