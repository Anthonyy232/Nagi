using FluentAssertions;
using Nagi.Core.Metadata;
using Xunit;

namespace Nagi.Core.Tests.Metadata;

public class FileExtensionsTests
{
    [Fact]
    public void MusicFileExtensions_MatchesCaseInsensitively()
    {
        FileExtensions.MusicFileExtensions.Should().Contain(".AA");
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
