using System.Collections.Specialized;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Presentation.Models;
using NSubstitute;
using Xunit;

namespace Nagi.WinUI.Tests;

public class PlaybackQueueCollectionTests
{
    [Fact]
    public void SkipAndPrevious_RemoveAndInsertRowsWithoutReplacingRemainingSongs()
    {
        var songs = Enumerable.Range(0, 4).Select(index => new Song { Title = $"Song {index}" }).ToArray();
        var playback = Substitute.For<IMusicPlaybackService>();
        playback.PlaybackQueue.Returns(songs.Select(song => song.Id).ToArray());
        playback.GetQueueSongsAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(songs.ToDictionary(song => song.Id));
        using var queue = new PlaybackQueueCollection(playback, NullLogger.Instance);
        queue.SetActive(true);
        var originalRows = queue.ToArray();
        var changes = new List<NotifyCollectionChangedAction>();
        queue.CollectionChanged += (_, args) => changes.Add(args.Action);

        playback.CurrentQueueIndex.Returns(1);
        queue.Refresh();

        changes.Should().Equal(NotifyCollectionChangedAction.Remove);
        queue.Should().Equal(originalRows.Skip(1));
        changes.Clear();

        playback.CurrentQueueIndex.Returns(0);
        queue.Refresh();

        changes.Should().Equal(NotifyCollectionChangedAction.Add);
        queue[0].Id.Should().Be(songs[0].Id);
        queue.Skip(1).Should().Equal(originalRows.Skip(1));
    }

    [Fact]
    public async Task UpNext_ReorderingKeepsCurrentTrackFirstAndUsesUpcomingSongsForDuration()
    {
        var songs = Enumerable.Range(0, 5).Select(index => new Song { Title = $"Song {index}" }).ToArray();
        var ids = songs.Select(song => song.Id).ToArray();
        var playback = Substitute.For<IMusicPlaybackService>();
        playback.PlaybackQueue.Returns(ids);
        playback.CurrentTrack.Returns(songs[2]);
        playback.CurrentQueueIndex.Returns(2);
        playback.GetQueueSongsAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(songs.ToDictionary(song => song.Id));
        playback.GetQueueDurationAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>()).Returns(TimeSpan.FromMinutes(3));
        using var queue = new PlaybackQueueCollection(playback, NullLogger.Instance);
        queue.SetShowTotalDuration(true);
        queue.SetActive(true);
        var current = queue[0];
        var next = queue[1];

        queue.Move(current, 1);
        queue.Move(next, -1);
        queue.Move(1, 0);
        queue.CompleteReorder(next, queue.Version);

        playback.DidNotReceive().MoveQueueItem(Arg.Any<Guid>(), Arg.Any<Guid?>());
        queue.Select(entry => entry.Id).Should().Equal(ids.Skip(2));
        queue.Move(queue[2], -1);
        playback.Received(1).MoveQueueItem(ids[4], ids[3]);
        await playback.Received(1).GetQueueDurationAsync(Arg.Is<IEnumerable<Guid>>(requested => requested.SequenceEqual(ids.Skip(2))), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void EmptyQueue_ShowsHelpfulEmptyState()
    {
        var playback = Substitute.For<IMusicPlaybackService>();
        playback.PlaybackQueue.Returns(Array.Empty<Guid>());
        using var queue = new PlaybackQueueCollection(playback, NullLogger.Instance);

        queue.SetActive(true);

        queue.ShowEmptyState.Should().BeTrue();
        queue.IsLoading.Should().BeFalse();
        queue.HasLoadError.Should().BeFalse();
        queue.HasMoreItems.Should().BeFalse();
    }

    [Fact]
    public async Task LoadingQueue_DistinguishesPendingWorkFromAnEmptyQueue()
    {
        var song = new Song { Title = "Pending" };
        var playback = Substitute.For<IMusicPlaybackService>();
        playback.PlaybackQueue.Returns(new[] { song.Id });
        var result = new TaskCompletionSource<IReadOnlyDictionary<Guid, Song>>(TaskCreationOptions.RunContinuationsAsynchronously);
        playback.GetQueueSongsAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>()).Returns(result.Task);
        using var queue = new PlaybackQueueCollection(playback, NullLogger.Instance);

        queue.SetActive(true);
        var loading = queue.LoadMoreItemsAsync();
        queue.IsLoading.Should().BeTrue();
        queue.ShowInitialLoading.Should().BeTrue();
        queue.ShowMoreLoading.Should().BeFalse();
        queue.ShowEmptyState.Should().BeFalse();
        result.SetResult(new Dictionary<Guid, Song> { [song.Id] = song });
        await loading.WaitAsync(TimeSpan.FromSeconds(10));

        queue.IsLoading.Should().BeFalse();
        queue.ShowEmptyState.Should().BeFalse();
        queue.Should().ContainSingle().Which.Song.Should().BeSameAs(song);
    }

    [Fact]
    public void FailedLoad_ShowsErrorAndCanBeRetried()
    {
        var song = new Song { Title = "Recovered" };
        var playback = Substitute.For<IMusicPlaybackService>();
        playback.PlaybackQueue.Returns(new[] { song.Id });
        playback.GetQueueSongsAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>()).Returns(
            Task.FromException<IReadOnlyDictionary<Guid, Song>>(new IOException("Unavailable")),
            Task.FromResult<IReadOnlyDictionary<Guid, Song>>(new Dictionary<Guid, Song> { [song.Id] = song }));
        using var queue = new PlaybackQueueCollection(playback, NullLogger.Instance);

        queue.SetActive(true);

        queue.HasLoadError.Should().BeTrue();
        queue.IsLoading.Should().BeFalse();
        queue.ShowEmptyState.Should().BeFalse();
        queue.HasMoreItems.Should().BeFalse("incremental loading should wait for the user to retry");

        queue.Refresh(reloadMetadata: true);

        queue.HasLoadError.Should().BeFalse();
        queue.Should().ContainSingle().Which.Song.Should().BeSameAs(song);
    }

    [Fact]
    public async Task Refresh_WhenPreviousLoadFinishesLate_KeepsLatestQueueAndState()
    {
        var oldSong = new Song { Title = "Old" };
        var newSong = new Song { Title = "New" };
        var playback = Substitute.For<IMusicPlaybackService>();
        playback.PlaybackQueue.Returns(new[] { oldSong.Id });
        var oldResult = new TaskCompletionSource<IReadOnlyDictionary<Guid, Song>>(TaskCreationOptions.RunContinuationsAsynchronously);
        playback.GetQueueSongsAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>()).Returns(
            oldResult.Task,
            Task.FromResult<IReadOnlyDictionary<Guid, Song>>(new Dictionary<Guid, Song> { [newSong.Id] = newSong }));
        using var queue = new PlaybackQueueCollection(playback, NullLogger.Instance);
        queue.SetActive(true);
        var oldLoad = queue.LoadMoreItemsAsync();

        playback.PlaybackQueue.Returns(new[] { newSong.Id });
        queue.Refresh();
        oldResult.SetResult(new Dictionary<Guid, Song> { [oldSong.Id] = oldSong });
        await oldLoad.WaitAsync(TimeSpan.FromSeconds(10));

        queue.Should().ContainSingle().Which.Song.Should().BeSameAs(newSong);
        queue.IsLoading.Should().BeFalse();
        queue.HasLoadError.Should().BeFalse();
    }

    [Fact]
    public async Task IncrementalLoad_AfterFailure_RetryLoadsAllTracksWithoutDuplicates()
    {
        var songs = Enumerable.Range(0, 130).Select(i => new Song { Title = $"Track {i}" }).ToDictionary(song => song.Id);
        var playback = Substitute.For<IMusicPlaybackService>();
        playback.PlaybackQueue.Returns(songs.Keys.ToArray());
        var attempts = 0;
        var pending = new TaskCompletionSource<IReadOnlyDictionary<Guid, Song>>(TaskCreationOptions.RunContinuationsAsynchronously);
        playback.GetQueueSongsAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>()).Returns(call =>
            ++attempts == 2
                ? pending.Task
                : Task.FromResult<IReadOnlyDictionary<Guid, Song>>(call.Arg<IEnumerable<Guid>>().ToDictionary(id => id, id => songs[id])));
        using var queue = new PlaybackQueueCollection(playback, NullLogger.Instance);
        queue.SetActive(true);

        var loading = queue.LoadMoreItemsAsync();
        queue.ShowInitialLoading.Should().BeFalse();
        queue.ShowMoreLoading.Should().BeTrue();
        pending.SetException(new IOException("Unavailable"));
        await loading.WaitAsync(TimeSpan.FromSeconds(10));

        queue.HasLoadError.Should().BeTrue();
        queue.Should().HaveCount(128);
        queue.Refresh(reloadMetadata: true);
        await queue.LoadMoreItemsAsync();

        queue.HasLoadError.Should().BeFalse();
        queue.HasMoreItems.Should().BeFalse();
        queue.Select(entry => entry.Id).Should().Equal(songs.Keys);
    }

    [Fact]
    public async Task Reopening_ReusesRowsUntilLibraryMetadataChanges()
    {
        var song = new Song { Title = "Original" };
        var updated = new Song { Id = song.Id, Title = "Updated" };
        var playback = Substitute.For<IMusicPlaybackService>();
        playback.PlaybackQueue.Returns(new[] { song.Id });
        playback.GetQueueSongsAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>()).Returns(
            Task.FromResult<IReadOnlyDictionary<Guid, Song>>(new Dictionary<Guid, Song> { [song.Id] = song }),
            Task.FromResult<IReadOnlyDictionary<Guid, Song>>(new Dictionary<Guid, Song> { [song.Id] = updated }));
        playback.GetQueueDurationAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>()).Returns(TimeSpan.FromSeconds(90));
        using var queue = new PlaybackQueueCollection(playback, NullLogger.Instance);
        queue.SetShowTotalDuration(true);
        queue.SetActive(true);
        var originalEntry = queue.Single();

        queue.SetActive(false);
        queue.SetActive(true);
        queue.Should().ContainSingle().Which.Should().BeSameAs(originalEntry);
        queue.TotalDurationText.Should().Be("0:01:30");
        await playback.Received(1).GetQueueDurationAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>());
        await playback.Received(1).GetQueueSongsAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>());

        queue.SetActive(false);
        queue.Refresh(reloadMetadata: true);
        queue.SetActive(true);
        queue.Should().ContainSingle().Which.Song.Should().BeSameAs(updated);
        await playback.Received(2).GetQueueSongsAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Reorder_ReusesMetadataAndUpdatesDisplayedPositions()
    {
        var songs = Enumerable.Range(0, 3).Select(i => new Song { Title = $"Track {i}" }).ToDictionary(song => song.Id);
        var playback = Substitute.For<IMusicPlaybackService>();
        playback.PlaybackQueue.Returns(songs.Keys.ToArray());
        playback.GetQueueSongsAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>()).Returns(songs);
        using var queue = new PlaybackQueueCollection(playback, NullLogger.Instance);
        queue.SetActive(true);
        var originalEntries = queue.ToArray();

        playback.PlaybackQueue.Returns(songs.Keys.Reverse().ToArray());
        queue.Refresh();

        queue.Should().Equal(originalEntries.Reverse());
        queue.Select(entry => entry.Position).Should().Equal(1, 2, 3);
        await playback.Received(1).GetQueueSongsAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>());
    }

}
