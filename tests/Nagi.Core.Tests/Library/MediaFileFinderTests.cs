using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nagi.Core.Library;
using Nagi.Core.Platform;
using NSubstitute;
using Xunit;

namespace Nagi.Core.Tests.Library;

public class MediaFileFinderTests
{
    [Fact]
    public void FindCoverArt_TakesTheNearestCoverAboveTheSongButNoneAboveTheLibraryFolder()
    {
        var fileSystem = Substitute.For<IFileSystemService>();
        fileSystem.GetFiles(@"C:\Music\Artist", "*.*").Returns([@"C:\Music\Artist\notes.jpg", @"C:\Music\Artist\folder.jpg"]);
        fileSystem.GetFiles(@"C:\", "*.*").Returns([@"C:\cover.jpg"]);
        var finder = new MediaFileFinder(fileSystem, NullLogger.Instance);

        finder.FindCoverArt(@"C:\Music\Artist\Album\Disc 1\song.mp3", @"C:\Music\").Should().Be(@"C:\Music\Artist\folder.jpg");
        finder.FindCoverArt(@"C:\Music\Other\song.mp3", @"C:\Music\").Should().BeNull();
    }
}
