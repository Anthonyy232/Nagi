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
    }

    [Fact]
    public void FindImage_WhenMatchingFileExists_ReturnsFirstMatchingPath()
    {
        var dir = "C:\\images";
        var jpgPath = Path.Combine(dir, "abc.custom.jpg");

        _fs.DirectoryExists(dir).Returns(true);
        _fs.FileExists(Path.Combine(dir, "abc.custom.png")).Returns(true);
        _fs.FileExists(jpgPath).Returns(true);

        var result = ImageStorageHelper.FindImage(_fs, dir, "abc", ".custom");

        result.Should().Be(jpgPath);
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

    [Theory]
    [InlineData("success")]
    [InlineData("write")]
    [InlineData("replace")]
    public async Task SaveImageBytesAsync_PreservesPreviousArtworkUntilReplacementSucceeds(string outcome)
    {
        var directory = Directory.CreateTempSubdirectory("Nagi-Artwork-");
        try
        {
            var jpg = Path.Combine(directory.FullName, "abc.custom.jpg");
            var png = Path.Combine(directory.FullName, "abc.custom.png");
            byte[] previous = [1, 2, 3];
            byte[] replacement = [4, 5, 6];
            await File.WriteAllBytesAsync(jpg, previous);
            await File.WriteAllBytesAsync(png, previous);
            _fs.DirectoryExists(Arg.Any<string>()).Returns(call => Directory.Exists(call.Arg<string>()));
            _fs.FileExists(Arg.Any<string>()).Returns(call => File.Exists(call.Arg<string>()));
            _fs.When(fs => fs.DeleteFile(Arg.Any<string>())).Do(call => File.Delete(call.Arg<string>()));
            _fs.WriteAllBytesAsync(Arg.Any<string>(), Arg.Any<byte[]>()).Returns(async call =>
            {
                await File.WriteAllBytesAsync(call.Arg<string>(), call.Arg<byte[]>());
                if (outcome == "write") throw new IOException("Write failed");
            });
            _fs.When(fs => fs.MoveFile(Arg.Any<string>(), Arg.Any<string>(), true)).Do(call =>
            {
                if (outcome == "replace") throw new IOException("Replacement failed");
                File.Move(call.ArgAt<string>(0), call.ArgAt<string>(1), true);
            });

            var save = () => ImageStorageHelper.SaveImageBytesAsync(_fs, directory.FullName, "abc", ".custom", replacement, ".png");
            if (outcome == "success")
            {
                (await save()).Should().Be(png);
                Directory.GetFiles(directory.FullName).Should().Equal(png);
                (await File.ReadAllBytesAsync(png)).Should().Equal(replacement);
            }
            else
            {
                await save.Should().ThrowAsync<IOException>();
                Directory.GetFiles(directory.FullName).Should().BeEquivalentTo([jpg, png]);
                (await File.ReadAllBytesAsync(jpg)).Should().Equal(previous);
                (await File.ReadAllBytesAsync(png)).Should().Equal(previous);
            }
        }
        finally
        {
            directory.Delete(true);
        }
    }
}
