using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nagi.Core.Platform;
using Nagi.Core.Playback;
using NSubstitute;
using Xunit;

namespace Nagi.Core.Tests.Playback;

public class PlaybackStateStoreTests
{
    [Fact]
    public async Task FailedReplacementPreservesSavedQueueAndCleansTemporaryFile()
    {
        var directory = Directory.CreateTempSubdirectory("Nagi-playback-");
        try
        {
            var path = Path.Combine(directory.FullName, "playback_state.json");
            var store = CreateStore(path);
            var songId = Guid.NewGuid();
            await store.SaveAsync(new PlaybackState { PositionId = songId, PlaybackQueueTrackIds = [songId] });

            using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var failure = await Record.ExceptionAsync(() => store.SaveAsync(new PlaybackState()));
                Assert.True(failure is IOException or UnauthorizedAccessException, failure?.ToString());
            }

            var state = await store.LoadAsync();
            state!.PositionId.Should().Be(songId);
            state.PlaybackQueueTrackIds.Should().Equal(songId);
            Directory.GetFiles(directory.FullName).Should().Equal(path);
            store.Clear();
            (await store.LoadAsync()).Should().BeNull();
            File.Exists(path).Should().BeFalse();
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ReadFailurePreservesFileButMalformedJsonIsDiscarded()
    {
        var directory = Directory.CreateTempSubdirectory("Nagi-playback-");
        try
        {
            var path = Path.Combine(directory.FullName, "playback_state.json");
            var store = CreateStore(path);
            await store.SaveAsync(new PlaybackState());
            using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None))
                (await store.LoadAsync()).Should().BeNull();
            File.Exists(path).Should().BeTrue();

            await File.WriteAllTextAsync(path, "{broken");
            (await store.LoadAsync()).Should().BeNull();
            File.Exists(path).Should().BeFalse();
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static PlaybackStateStore CreateStore(string path)
    {
        var paths = Substitute.For<IPathConfiguration>();
        paths.PlaybackStateFilePath.Returns(path);
        return new PlaybackStateStore(paths, NullLogger<PlaybackStateStore>.Instance);
    }
}
