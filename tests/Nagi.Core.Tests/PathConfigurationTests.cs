using FluentAssertions;
using Nagi.Core.Helpers;
using Xunit;

namespace Nagi.Core.Tests;

public class PathConfigurationTests
{
    [Fact]
    public void PlacesEverythingUnderTheGivenRootAndCreatesTheFolders()
    {
        var root = Path.Combine(Path.GetTempPath(), "nagi-paths-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new PathConfiguration(root);

            new[]
            {
                paths.DatabasePath, paths.PlaybackStateFilePath, paths.AlbumArtCachePath, paths.ArtistImageCachePath,
                paths.PlaylistImageCachePath, paths.LrcCachePath, paths.RomanizationPacksPath, paths.LogsDirectory
            }.Should().OnlyContain(path => Path.GetDirectoryName(path) == root);
            new[]
            {
                paths.AlbumArtCachePath, paths.ArtistImageCachePath, paths.PlaylistImageCachePath, paths.LrcCachePath,
                paths.RomanizationPacksPath, paths.LogsDirectory
            }.Should().OnlyContain(path => Directory.Exists(path));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
