using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;
using Nagi.WinUI.Services.Abstractions;
using Nagi.WinUI.ViewModels;
using NSubstitute;
using Xunit;

namespace Nagi.WinUI.Tests;

public class SongListViewModelTests
{
    [Fact]
    public async Task ReloadWithSelectAll_UpdatesSelectionUsingTheNewTotal()
    {
        using var viewModel = new TestSongList((page, size, _) => Task.FromResult(new PagedResult<Song>
        {
            PageNumber = page, PageSize = size, TotalCount = 1, Items = [new Song()]
        }));
        viewModel.SelectAll();

        await viewModel.LoadPageAsync(1);

        viewModel.IsSingleSongSelected.Should().BeTrue();
        viewModel.PlaySelectedSongsCommand.CanExecute(null).Should().BeTrue();
        viewModel.SelectedItemsCountText.Should().NotBeEmpty();
    }

    [Fact]
    public async Task LoadPageAsync_WhenRequestedPageWasRemoved_LoadsLastRemainingPage()
    {
        var song = new Song { Title = "Last song" };
        var pages = new List<int>();
        using var viewModel = new TestSongList((page, size, _) =>
        {
            pages.Add(page);
            return Task.FromResult(new PagedResult<Song>
            {
                PageNumber = page, PageSize = size, TotalCount = 51, Items = page == 2 ? [song] : []
            });
        });

        await viewModel.LoadPageAsync(3);

        pages.Should().Equal(3, 2);
        viewModel.CurrentPage.Should().Be(2);
        viewModel.Songs.Should().Equal(song);
        viewModel.HasNextPage.Should().BeFalse();
    }

    [Fact]
    public async Task LoadPageAsync_AfterFailure_ReportsErrorAndAllowsRetry()
    {
        var attempts = 0;
        var song = new Song { Title = "Recovered" };
        using var viewModel = new TestSongList((page, size, _) => ++attempts == 1
            ? Task.FromException<PagedResult<Song>>(new IOException("Temporary failure"))
            : Task.FromResult(new PagedResult<Song> { PageNumber = page, PageSize = size, TotalCount = 1, Items = [song] }));

        await viewModel.LoadPageAsync(1);
        viewModel.HasLoadError.Should().BeTrue();
        viewModel.IsLoading.Should().BeFalse();

        await viewModel.LoadPageAsync(1);
        viewModel.HasLoadError.Should().BeFalse();
        viewModel.Songs.Should().Equal(song);
    }

    [Fact]
    public async Task LoadPageAsync_WhenResetBeforeQueryCompletes_DiscardsResult()
    {
        var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var result = new TaskCompletionSource<PagedResult<Song>>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var viewModel = new TestSongList((_, _, token) =>
        {
            started.SetResult(token);
            return result.Task;
        });
        var loading = viewModel.LoadPageAsync(1);
        var token = await started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        viewModel.ResetState();
        result.SetResult(new PagedResult<Song> { PageNumber = 1, PageSize = 50, TotalCount = 1, Items = [new Song()] });
        await loading.WaitAsync(TimeSpan.FromSeconds(10));

        token.IsCancellationRequested.Should().BeTrue();
        viewModel.Songs.Should().BeEmpty();
        viewModel.HasLoadError.Should().BeFalse();
    }

    private sealed class TestSongList(Func<int, int, CancellationToken, Task<PagedResult<Song>>> load)
        : SongListViewModelBase(Substitute.For<ILibraryReader>(), Substitute.For<ILibraryWriter>(), Substitute.For<ILibraryScanner>(), Substitute.For<IPlaylistService>(),
            Substitute.For<IMusicPlaybackService>(),
            Substitute.For<IMusicNavigationService>(), new ImmediateDispatcher(), Substitute.For<IUISettingsService>(),
            Substitute.For<IUIService>(), NullLogger.Instance)
    {
        protected override Task<PagedResult<Song>> LoadSongsPagedAsync(int pageNumber, int pageSize,
            SongSortOrder sortOrder, CancellationToken cancellationToken = default) => load(pageNumber, pageSize, cancellationToken);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResolveSelection_WhenSelectionChangesDuringQuery_UsesOriginalSelection(bool selectAll)
    {
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        var pending = new TaskCompletionSource<List<Guid>>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var viewModel = new SelectionTestList(pending.Task);
        if (selectAll)
        {
            viewModel.SelectionState.SelectAll();
            viewModel.SelectionState.Deselect(ids[2]);
        }
        else viewModel.SelectionState.Select(ids[0]);

        var resolving = viewModel.ResolveSelectionAsync();
        viewModel.SelectionState.DeselectAll();
        viewModel.SelectionState.Select(ids[2]);
        pending.SetResult(ids.ToList());

        (await resolving.WaitAsync(TimeSpan.FromSeconds(10))).Should().Equal(selectAll ? ids.Take(2) : ids.Take(1));
    }

    [Fact]
    public async Task PlaySong_WhenViewChangesDuringQuery_DoesNotStartOldTrack()
    {
        var pending = new TaskCompletionSource<List<Guid>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var playback = Substitute.For<IMusicPlaybackService>();
        using var viewModel = new SelectionTestList(pending.Task, playback);
        var song = new Song();
        var playing = viewModel.PlaySongCommand.ExecuteAsync(song);

        viewModel.ResetState();
        pending.SetResult(new List<Guid> { song.Id });
        await playing.WaitAsync(TimeSpan.FromSeconds(10));

        await playback.DidNotReceive().PlayAsync(Arg.Any<Guid>());
        await playback.DidNotReceive().PlayAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<int>(), Arg.Any<bool?>(), Arg.Any<PlaybackContext?>());
    }

    [Fact]
    public async Task PlaySelected_WhenViewChangesDuringQuery_DoesNotReplacePlaybackWithEmptyQueue()
    {
        var pending = new TaskCompletionSource<List<Guid>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var playback = Substitute.For<IMusicPlaybackService>();
        using var viewModel = new SelectionTestList(pending.Task, playback);
        viewModel.SelectionState.SelectAll();
        var playing = viewModel.PlaySelectedSongsCommand.ExecuteAsync(null);

        viewModel.ResetState();
        pending.SetResult(new List<Guid> { Guid.NewGuid() });
        await playing.WaitAsync(TimeSpan.FromSeconds(10));

        await playback.DidNotReceive().PlayAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<int>(), Arg.Any<bool?>(), Arg.Any<PlaybackContext?>());
    }

    private sealed class SelectionTestList(Task<List<Guid>> ids, IMusicPlaybackService? playback = null)
        : SongListViewModelBase(Substitute.For<ILibraryReader>(), Substitute.For<ILibraryWriter>(), Substitute.For<ILibraryScanner>(), Substitute.For<IPlaylistService>(),
            playback ?? Substitute.For<IMusicPlaybackService>(),
            Substitute.For<IMusicNavigationService>(), new ImmediateDispatcher(), Substitute.For<IUISettingsService>(),
            Substitute.For<IUIService>(), NullLogger.Instance)
    {
        protected override Task<List<Guid>> LoadAllSongIdsAsync(SongSortOrder sortOrder, CancellationToken token = default) => ids;
        public Task<List<Guid>> ResolveSelectionAsync() => GetCurrentSelectionIdsAsync();
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public async Task ResolveSelection_WhenQueryCompletesAfterLeavingView_DiscardsResult(bool dispose, bool fails)
    {
        var pending = new TaskCompletionSource<List<Guid>>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var viewModel = new SelectionTestList(pending.Task);
        viewModel.SelectionState.SelectAll();
        var resolving = viewModel.ResolveSelectionAsync();

        if (dispose) viewModel.Dispose();
        else viewModel.ResetState();
        if (fails) pending.SetException(new IOException("The previous query failed"));
        else pending.SetResult([Guid.NewGuid()]);

        (await resolving.WaitAsync(TimeSpan.FromSeconds(10))).Should().BeEmpty();
    }

    [Fact]
    public async Task PlaySong_WithMissingTrackInCurrentList_StillPlaysRequestedTrack()
    {
        var playback = Substitute.For<IMusicPlaybackService>();
        using var viewModel = new SelectionTestList(Task.FromResult(new List<Guid>()), playback);
        var song = new Song();

        await viewModel.PlaySongCommand.ExecuteAsync(song);

        await playback.Received(1).PlayAsync(song.Id);
    }
}
