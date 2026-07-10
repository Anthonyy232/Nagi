using FluentAssertions;
using Nagi.Core.Helpers;
using Xunit;

namespace Nagi.Core.Tests;

public class FileNameHelperTests
{
    [Fact]
    public void GenerateLrcCacheFileName_SameMetadataDifferentFilesProducesDifferentNames()
    {
        var first = FileNameHelper.GenerateLrcCacheFileName(
            @"C:\Music\Disc 1\Song.flac", "Artist", "Album", "Song");
        var second = FileNameHelper.GenerateLrcCacheFileName(
            @"C:\Music\Disc 2\Song.flac", "Artist", "Album", "Song");

        first.Should().NotBe(second);
    }

    [Fact]
    public void GenerateLrcCacheFileName_PathFormattingDifferencesProduceSameName()
    {
        var first = FileNameHelper.GenerateLrcCacheFileName(
            @"c:\music\Song.flac", "Artist", "Album", "Song");
        var second = FileNameHelper.GenerateLrcCacheFileName(
            "C:/MUSIC/Song.flac", "Artist", "Album", "Song");

        first.Should().Be(second);
    }

    [Fact]
    public void GenerateLrcCacheFileName_CapsLongMetadataPrefix()
    {
        var longValue = new string('x', 300);

        var result = FileNameHelper.GenerateLrcCacheFileName(
            @"C:\Music\Song.flac", longValue, longValue, longValue);

        result.Should().EndWith(".lrc");
        result.Length.Should().BeLessThanOrEqualTo(171);
    }
}
