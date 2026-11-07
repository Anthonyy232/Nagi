using FluentAssertions;
using Nagi.Core.Library;
using Nagi.Core.Services.Implementations;
using Xunit;

namespace Nagi.Core.Tests;

public class PlaybackQueueTests
{
    private static readonly Guid A = Guid.NewGuid(), B = Guid.NewGuid(), C = Guid.NewGuid(), D = Guid.NewGuid();

    private static PlaybackQueue Create(Guid? current, params Guid[] order)
    {
        var queue = new PlaybackQueue();
        queue.Replace([.. order], shuffle: false, current);
        return queue;
    }

    [Theory]
    [InlineData(0, 2, new[] { 1, 0, 2, 3 })]
    [InlineData(3, 0, new[] { 3, 0, 1, 2 })]
    [InlineData(0, -1, new[] { 1, 2, 3, 0 })]
    public void Move_PlacesTheSongBeforeTheTarget(int song, int before, int[] expected)
    {
        Guid[] ids = [A, B, C, D];
        var queue = Create(null, ids);

        var moved = queue.Move(ids[song], before < 0 ? null : ids[before]);

        moved.Should().BeTrue();
        queue.PlayOrder.Should().Equal(expected.Select(i => ids[i]));
        queue.PositionOf(ids[song]).Should().Be(Array.IndexOf(expected, song));
    }

    [Fact]
    public void Move_IntoItsOwnPositionIsANoOp()
    {
        var queue = Create(null, A, B, C);

        queue.Move(A, B).Should().BeFalse();
        queue.PlayOrder.Should().Equal(A, B, C);
    }

    [Fact]
    public void Move_WhileShuffled_ReordersOnlyThePlayOrder()
    {
        var queue = new PlaybackQueue();
        queue.Replace([A, B, C], shuffle: true, startId: null, shuffledOrder: [C, A, B]);

        queue.Move(B, A);

        queue.PlayOrder.Should().Equal(C, B, A);
        queue.Order.Should().Equal(A, B, C);
    }

    [Fact]
    public void MoveAfterCurrent_InsertsAfterTheCurrentSongInTheGivenOrder()
    {
        var queue = Create(C, A, B, C, D);

        queue.MoveAfterCurrent([D, A, C]);

        queue.PlayOrder.Should().Equal(B, C, D, A);
        queue.CurrentId.Should().Be(C);
    }

    [Fact]
    public void Add_WhileShuffled_AppendsOnlyNewSongsToBothOrders()
    {
        var queue = new PlaybackQueue();
        queue.Replace([A, B], shuffle: true, startId: null, shuffledOrder: [B, A]);

        queue.Add([A, C, D]).Should().BeTrue();

        queue.Order.Should().Equal(A, B, C, D);
        queue.PlayOrder.Take(2).Should().Equal(B, A);
        queue.PlayOrder.Should().BeEquivalentTo([A, B, C, D]);
        queue.PositionOf(queue.PlayOrder[3]).Should().Be(3);
    }

    [Fact]
    public void Remove_TheCurrentSong_MovesToTheNextRemainingSong()
    {
        var queue = Create(B, A, B, C, D);

        queue.Remove(new HashSet<Guid> { B, C });

        queue.PlayOrder.Should().Equal(A, D);
        queue.CurrentId.Should().Be(D);
        queue.CurrentPosition.Should().Be(1);
    }

    [Fact]
    public void Replace_WhileShuffling_PlaysTheChosenSongFirst()
    {
        var queue = new PlaybackQueue();

        queue.Replace([A, B, C, D], shuffle: true, startId: C);

        queue.PlayOrder[0].Should().Be(C);
        queue.CurrentId.Should().Be(C);
        queue.Order.Should().Equal(A, B, C, D);
    }

    [Theory]
    [InlineData(2, true, false, -1)]
    [InlineData(2, true, true, 0)]
    [InlineData(0, false, true, 2)]
    [InlineData(1, true, false, 2)]
    public void GetAdjacent_FollowsPlayOrderAndWrapsOnlyWhenAsked(int from, bool forward, bool wrap, int expected)
    {
        Guid[] ids = [A, B, C];
        var queue = Create(ids[from], ids);

        queue.GetAdjacent(forward, wrap).Should().Be(expected < 0 ? null : ids[expected]);
    }

    [Fact]
    public void GetAdjacent_FollowsTheShuffledOrder()
    {
        var queue = new PlaybackQueue();
        queue.Replace([A, B, C], shuffle: true, startId: null, shuffledOrder: [C, A, B]);

        queue.GetAdjacent(forward: true, wrap: false).Should().Be(A);
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(true, 0)]
    public void FindFromCurrent_SkipsUnmatchedSongsAndWrapsOnlyWhenAsked(bool wrap, int? expectedIndex)
    {
        Guid[] ids = [A, B, C, D];
        var queue = Create(B, ids);
        var removed = new HashSet<Guid> { B, C, D };

        var next = queue.FindFromCurrent(id => !removed.Contains(id), wrap);

        next.Should().Be(expectedIndex is null ? null : ids[expectedIndex.Value]);
    }

    [Theory]
    [InlineData(false, new[] { 2, 3 })]
    [InlineData(true, new[] { 2, 3, 0, 1 })]
    public void Upcoming_StartsAtTheCurrentSong(bool wrap, int[] expected)
    {
        Guid[] ids = [A, B, C, D];
        var queue = Create(C, ids);

        queue.Upcoming(wrap).Should().Equal(expected.Select(i => ids[i]));
    }

    [Fact]
    public void Order_ReplacingTheQueueLeavesEarlierViewsOnTheOldList()
    {
        var queue = Create(null, A, B);
        var before = queue.Order;

        queue.Replace([C], shuffle: false, startId: null);

        before.Should().Equal(A, B);
        queue.Order.Should().Equal(C);
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
