using AwesomeAssertions;
using Nagi.Core.Metadata;
using Xunit;

namespace Nagi.Core.Tests.Metadata;

public class SongFileMetadataTests
{
    [Theory]
    [InlineData(new[] { "Aaa/Bbb" }, new[] { "Aaa/Bbb", null })]
    [InlineData(new[] { "Aaa", "Bbb", "Ccc" }, new string?[0])]
    public void NormalizeTags_GivesASortTagThatDoesNotPairToThePrimaryArtistOnly(string[] sortNames, string?[] expected)
    {
        var metadata = new SongFileMetadata { Artists = ["Zed/Abba"], ArtistSortNames = [.. sortNames] };

        metadata.NormalizeTags("/", string.Empty);

        metadata.ArtistSortNames.Should().Equal(expected);
    }
}
