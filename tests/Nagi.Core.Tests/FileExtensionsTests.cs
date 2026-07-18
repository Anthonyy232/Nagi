using FluentAssertions;
using Nagi.Core.Constants;
using Xunit;

namespace Nagi.Core.Tests;

public class FileExtensionsTests
{
    [Theory]
    [InlineData(".opus")]
    [InlineData(".webm")]
    [InlineData(".mpc")]
    [InlineData(".mpp")]
    [InlineData(".AA")]
    public void MusicFileExtensions_ContainsVerifiedPlaybackFormats(string extension)
    {
        FileExtensions.MusicFileExtensions.Should().Contain(extension);
    }

    [Theory]
    [InlineData(".aax")]
    [InlineData(".m4p")]
    [InlineData(".m2v")]
    [InlineData(".mpv")]
    public void MusicFileExtensions_DoesNotAdvertiseEncryptedOrVideoOnlyFormats(string extension)
    {
        FileExtensions.MusicFileExtensions.Should().NotContain(extension);
    }
}
