using FluentAssertions;
using Nagi.Core.Artwork;
using Xunit;

namespace Nagi.Core.Tests.Artwork;

public class ArtworkUriTests
{
    private static readonly DateTime Modified = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);

    [Theory]
    [InlineData(@"C:\cache\artist.custom.jpg", "file:///C:/cache/artist.custom.jpg?v={0}")]
    [InlineData("https://example.com/cover.jpg?size=large#top", "https://example.com/cover.jpg?size=large&v={0}#top")]
    public void Versioned_AddsTheStampWithoutLosingTheQuery(string path, string expected) =>
        ArtworkUri.Versioned(path, Modified).Should().Be(string.Format(expected, Modified.Ticks));

    [Fact]
    public void Versioned_ChangesWithTheStampAndKeepsAPathWithout()
    {
        const string path = @"C:\cache\playlist.custom.jpg";

        ArtworkUri.Versioned(path, Modified).Should().NotBe(ArtworkUri.Versioned(path, Modified.AddSeconds(1)));
        ArtworkUri.Versioned(path, null).Should().Be(path);
        ArtworkUri.Versioned("", Modified).Should().BeNull();
    }
}
