using FluentAssertions;
using Nagi.Core.Helpers;
using Xunit;

namespace Nagi.Core.Tests;

public class LrcCacheTests
{
    private const string CacheRoot = @"C:\Lyrics";

    [Fact]
    public void CachedPath_SameMetadataDifferentFilesProducesDifferentNames()
    {
        var first = LrcCache.CachedPath(CacheRoot, @"C:\Music\Disc 1\Song.flac", "Artist", "Album", "Song");
        var second = LrcCache.CachedPath(CacheRoot, @"C:\Music\Disc 2\Song.flac", "Artist", "Album", "Song");

        first.Should().NotBe(second);
    }

    [Fact]
    public void CachedPath_PathFormattingDifferencesProduceSameName()
    {
        var first = LrcCache.CachedPath(CacheRoot, @"c:\music\Song.flac", "Artist", "Album", "Song");
        var second = LrcCache.CachedPath(CacheRoot, "C:/MUSIC/Song.flac", "Artist", "Album", "Song");

        first.Should().Be(second);
    }

    [Fact]
    public void CachedPath_CapsLongMetadataPrefix()
    {
        var longValue = new string('x', 300);

        var result = Path.GetFileName(LrcCache.CachedPath(CacheRoot, @"C:\Music\Song.flac", longValue, longValue, longValue));

        result.Should().EndWith(".lrc");
        result.Length.Should().BeLessThanOrEqualTo(171);
    }
}
