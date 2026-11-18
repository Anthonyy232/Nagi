using AwesomeAssertions;
using Nagi.Core.Library;
using Nagi.Core.Playback;
using Xunit;

namespace Nagi.Core.Tests.Playback;

public class TransientTrackCacheTests
{
    [Fact]
    public void FindsByPathIgnoringCaseAndForgetsUnretainedTracks()
    {
        var cache = new TransientTrackCache();
        var kept = new Song { FilePath = @"C:\Music\Kept.mp3" };
        var dropped = new Song { FilePath = @"C:\Music\Dropped.mp3" };
        cache.GetOrAdd(kept);
        cache.GetOrAdd(dropped);

        cache.RetainOnly(new HashSet<Guid> { kept.Id });

        cache.TryGetByPath(@"c:\music\KEPT.mp3", out var found).Should().BeTrue();
        found.Should().BeSameAs(kept);
        cache.ContainsKey(dropped.Id).Should().BeFalse();
        cache.TryGetByPath(dropped.FilePath, out _).Should().BeFalse();
    }
}
