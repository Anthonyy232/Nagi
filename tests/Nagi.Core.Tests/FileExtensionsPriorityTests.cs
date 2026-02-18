using FluentAssertions;
using Nagi.Core.Constants;
using Xunit;

namespace Nagi.Core.Tests;

/// <summary>
///     Verifies the Navidrome-compatible priority constants and ordered name lists in
///     <see cref="FileExtensions" />. These are the single source of truth consumed by
///     every cover-art and artist-image lookup method.
/// </summary>
public class FileExtensionsPriorityTests
{
    [Fact]
    public void CoverArtFileNamePriority_HasNavidromePriorityOrder()
    {
        // Priority must be: cover (0), folder (1), front (2)
        FileExtensions.CoverArtFileNamePriority[0].Should().Be("cover");
        FileExtensions.CoverArtFileNamePriority[1].Should().Be("folder");
        FileExtensions.CoverArtFileNamePriority[2].Should().Be("front");
        FileExtensions.CoverArtFileNamePriority.Should().HaveCount(3);
    }

    [Fact]
    public void CoverArtFileNames_IsDerivedFromPriority_ContainsAllNames()
    {
        foreach (var name in FileExtensions.CoverArtFileNamePriority)
        {
            FileExtensions.CoverArtFileNames.Should().Contain(name,
                because: $"'{name}' is in the priority list and must be in the HashSet");
        }

        FileExtensions.CoverArtFileNames.Should().HaveCount(FileExtensions.CoverArtFileNamePriority.Count);
    }

    [Theory]
    [InlineData("Cover")]
    [InlineData("FOLDER")]
    [InlineData("Front")]
    public void CoverArtFileNames_IsCaseInsensitive(string name)
    {
        FileExtensions.CoverArtFileNames.Should().Contain(name);
    }

    [Fact]
    public void ArtistImageFileNamePriority_ContainsOnlyArtist()
    {
        FileExtensions.ArtistImageFileNamePriority.Should().HaveCount(1);
        FileExtensions.ArtistImageFileNamePriority[0].Should().Be("artist");
    }

    [Theory]
    [InlineData("artist")]
    [InlineData("Artist")]
    [InlineData("ARTIST")]
    public void ArtistImageFileNames_IsCaseInsensitive(string name)
    {
        FileExtensions.ArtistImageFileNames.Should().Contain(name);
    }

    [Fact]
    public void ArtistImageFileNames_IsDerivedFromPriority()
    {
        foreach (var name in FileExtensions.ArtistImageFileNamePriority)
        {
            FileExtensions.ArtistImageFileNames.Should().Contain(name);
        }

        FileExtensions.ArtistImageFileNames.Should().HaveCount(FileExtensions.ArtistImageFileNamePriority.Count);
    }

    [Theory]
    [InlineData("artist", 0)]
    [InlineData("Artist", 0)]
    [InlineData("invalid", int.MaxValue)]
    public void GetArtistArtPriority_ReturnsCorrectPriority(string name, int expectedPriority)
    {
        FileExtensions.GetArtistArtPriority(name).Should().Be(expectedPriority);
    }

    [Fact]
    public void NonCoverArtNames_AreNotInCoverArtFileNames()
    {
        FileExtensions.CoverArtFileNames.Should().NotContain("artist");
        FileExtensions.CoverArtFileNames.Should().NotContain("thumb");
        FileExtensions.CoverArtFileNames.Should().NotContain("back");
    }

    [Fact]
    public void CoverArtNames_AreNotInArtistImageFileNames()
    {
        FileExtensions.ArtistImageFileNames.Should().NotContain("cover");
        FileExtensions.ArtistImageFileNames.Should().NotContain("folder");
        FileExtensions.ArtistImageFileNames.Should().NotContain("album");
    }
}
