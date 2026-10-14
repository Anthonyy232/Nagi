using FluentAssertions;
using Nagi.Core.Models;
using Nagi.Core.Services.Implementations;
using Xunit;

namespace Nagi.Core.Tests;

public class PlaybackQueueTests
{
    private static readonly Guid A = Guid.NewGuid(), B = Guid.NewGuid(), C = Guid.NewGuid(), D = Guid.NewGuid();

    private static PlaybackQueue Create(params Guid[] order)
    {
        var queue = new PlaybackQueue();
        queue.SetOrder([.. order]);
        queue.RebuildIndex();
        return queue;
    }

    [Theory]
    [InlineData(0, 2, new[] { 1, 0, 2, 3 })]
    [InlineData(3, 0, new[] { 3, 0, 1, 2 })]
    [InlineData(0, -1, new[] { 1, 2, 3, 0 })]
    public void Move_PlacesTheSongBeforeTheTarget(int song, int before, int[] expected)
    {
        Guid[] ids = [A, B, C, D];
        var queue = Create(ids);

        var moved = queue.Move(ids[song], before < 0 ? null : ids[before], inShuffled: false);

        moved.Should().BeTrue();
        queue.Order.Should().Equal(expected.Select(i => ids[i]));
    }

    [Fact]
    public void Move_IntoItsOwnPositionIsANoOp()
    {
        var queue = Create(A, B, C);

        queue.Move(A, B, inShuffled: false).Should().BeFalse();
        queue.Order.Should().Equal(A, B, C);
    }

    [Fact]
    public void MoveAfterCurrent_InsertsAfterThePlayingTrack()
    {
        var queue = Create(A, B, C, D);

        queue.MoveAfterCurrent(A, currentTrackId: C, stoppedIndex: -1, inShuffled: false);

        queue.Order.Should().Equal(B, C, A, D);
    }

    [Fact]
    public void MoveAfterCurrent_UsesTheStoppedIndexWhenNothingIsPlaying()
    {
        var queue = Create(A, B, C);

        queue.MoveAfterCurrent(D, currentTrackId: null, stoppedIndex: 0, inShuffled: false);

        queue.Order.Should().Equal(A, D, B, C);
    }

    [Fact]
    public void SyncShuffledWithOrder_DropsRemovedSongsAndAppendsNewOnes()
    {
        var queue = Create(A, B, C);
        queue.SetShuffled([C, A, B]);
        queue.SetOrder([A, C, D]);

        queue.SyncShuffledWithOrder();

        queue.Shuffled.Take(2).Should().Equal(C, A);
        queue.Shuffled.Should().BeEquivalentTo([A, C, D]);
        queue.ShuffledIndexOf(D).Should().Be(2);
    }

    [Theory]
    [InlineData(2, true, false, -1)]
    [InlineData(2, true, true, 0)]
    [InlineData(0, false, true, 2)]
    [InlineData(1, true, false, 2)]
    public void GetAdjacentIndex_FollowsPlayOrderAndRepeat(int from, bool forward, bool repeatAll, int expected)
    {
        var queue = Create(A, B, C);

        queue.GetAdjacentIndex(from, forward, shuffle: false, repeatAll).Should().Be(expected);
    }

    [Fact]
    public void GetAdjacentIndex_FollowsTheShuffledOrderButReturnsPlayOrderIndexes()
    {
        var queue = Create(A, B, C);
        queue.SetShuffled([C, A, B]);
        queue.RebuildShuffledIndex();

        queue.GetAdjacentIndex(queue.IndexOf(C), moveForward: true, shuffle: true, repeatAll: false)
            .Should().Be(queue.IndexOf(A));
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(true, 0)]
    public void GetNextSurvivingSongId_SkipsRemovedSongsAndWrapsOnlyWhenRepeating(bool repeatAll, int? expectedIndex)
    {
        Guid[] ids = [A, B, C, D];
        var queue = Create(ids);

        var next = queue.GetNextSurvivingSongId(new HashSet<Guid> { B, C, D }, currentIndex: 1, shuffle: false, repeatAll);

        next.Should().Be(expectedIndex is null ? null : ids[expectedIndex.Value]);
    }

    [Fact]
    public void OrderView_ReplacingTheOrderLeavesEarlierViewsOnTheOldList()
    {
        var queue = Create(A, B);
        var before = queue.OrderView();

        queue.SetOrder([C]);

        before.Should().Equal(A, B);
        queue.OrderView().Should().Equal(C);
    }

    [Fact]
    public void TransientTrackCache_FindsByPathIgnoringCaseAndForgetsUnretainedTracks()
    {
        var cache = new TransientTrackCache();
        var kept = new Song { FilePath = @"C:\Music\Kept.mp3" };
        var dropped = new Song { FilePath = @"C:\Music\Dropped.mp3" };
        cache.Register(kept);
        cache.Register(dropped);

        cache.RetainOnly(new HashSet<Guid> { kept.Id });

        cache.TryGetByPath(@"c:\music\KEPT.mp3", out var found).Should().BeTrue();
        found.Should().BeSameAs(kept);
        cache.ContainsKey(dropped.Id).Should().BeFalse();
        cache.TryGetByPath(dropped.FilePath, out _).Should().BeFalse();
    }
}
