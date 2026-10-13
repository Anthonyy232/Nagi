using FluentAssertions;
using Nagi.Presentation.Helpers;
using Xunit;

namespace Nagi.Presentation.Tests;

public class ImageUriHelperTests
{
    private static readonly DateTime Modified = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void CachedFileVersion_IsReused_WhileExplicitModificationDateUsesNewVersion()
    {
        var directory = Directory.CreateTempSubdirectory("Nagi-ImageUri-");
        try
        {
            var path = Path.Combine(directory.FullName, "cover.png");
            File.WriteAllBytes(path, []);
            File.SetLastWriteTimeUtc(path, Modified);
            var cached = ImageUriHelper.GetUriWithCacheBuster(path);
            new Uri(cached!).Query.Should().Be($"?v={Modified.Ticks}");

            var updated = Modified.AddMinutes(1);
            File.SetLastWriteTimeUtc(path, updated);

            ImageUriHelper.GetUriWithCacheBuster(path).Should().Be(cached);
            new Uri(ImageUriHelper.GetUriWithCacheBuster(path, updated)!).Query.Should().Be($"?v={updated.Ticks}");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData("https://example.com/cover.png?size=large#art", "https://example.com/cover.png?size=large&v={0}#art")]
    [InlineData("https://example.com/cover.png?rev=1", "https://example.com/cover.png?rev=1&v={0}")]
    [InlineData("ms-appx:///Assets/cover.png#art", "ms-appx:///Assets/cover.png?v={0}#art")]
    public void CacheVersion_PreservesExistingQueryAndFragment(string input, string expected)
    {
        ImageUriHelper.GetUriWithCacheBuster(input, Modified)
            .Should().Be(string.Format(expected, Modified.Ticks));
    }

    [Theory]
    [InlineData("https://example.com/cover.png?v=existing")]
    [InlineData("https://example.com/cover.png?%76=existing#art")]
    public void CacheVersion_PreservesExplicitVersion(string input)
    {
        ImageUriHelper.GetUriWithCacheBuster(input, Modified).Should().Be(input);
    }

    [Fact]
    public void CacheVersion_PreservesSpecialCharactersInLocalFileName()
    {
        var directory = Directory.CreateTempSubdirectory("Nagi-ImageUri-");
        try
        {
            var path = Path.Combine(directory.FullName, "cover#100%.png");
            File.WriteAllBytes(path, []);

            var uri = new Uri(ImageUriHelper.GetUriWithCacheBuster(path, Modified)!);

            uri.LocalPath.Should().Be(path);
            uri.Query.Should().Be($"?v={Modified.Ticks}");
            uri.Fragment.Should().BeEmpty();
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
