using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Presentation.Services.Abstractions;
using Nagi.Presentation.ViewModels;
using NSubstitute;
using Xunit;

namespace Nagi.WinUI.Tests;

public class PagedListViewModelTests
{


    [Fact]
    public async Task LoadAsync_WhenLibraryBecomesEmpty_ResetsPagerToFirstPage()
    {
        using var viewModel = CreateViewModel((page, size, _) => Task.FromResult(new PagedResult<int>
        {
            PageNumber = page,
            PageSize = size,
            TotalCount = 0
        }));
        viewModel.CurrentPage = 3;

        await viewModel.LoadAsync(CancellationToken.None);

        viewModel.CurrentPage.Should().Be(1);
        viewModel.TotalPages.Should().Be(1);
        viewModel.HasPreviousPage.Should().BeFalse();
        viewModel.HasNextPage.Should().BeFalse();
        viewModel.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task LoadAsync_WhenLastPageIsRemoved_LoadsLastRemainingPage()
    {
        var requestedPages = new List<int>();
        using var viewModel = CreateViewModel((page, size, _) =>
        {
            requestedPages.Add(page);
            return Task.FromResult(new PagedResult<int>
            {
                PageNumber = page,
                PageSize = size,
                TotalCount = 51,
                Items = page == 2 ? [51] : []
            });
        });
        viewModel.CurrentPage = 3;

        await viewModel.LoadAsync(CancellationToken.None);

        requestedPages.Should().Equal(3, 2);
        viewModel.CurrentPage.Should().Be(2);
        viewModel.TotalPages.Should().Be(2);
        viewModel.Items.Should().Equal(51);
    }

    [Fact]
    public void ProcessPagedResult_WithPaginationDisabled_KeepsPagerDisabled()
    {
        using var viewModel = CreateViewModel((_, _, _) => throw new InvalidOperationException());
        viewModel.IsPaginationEnabled = false;

        viewModel.ApplyResult(new PagedResult<int> { PageNumber = 2, PageSize = 50, TotalCount = 200 });

        viewModel.CurrentPage.Should().Be(1);
        viewModel.TotalPages.Should().Be(1);
        viewModel.HasPreviousPage.Should().BeFalse();
        viewModel.HasNextPage.Should().BeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResetState_CancelsInflightLoadAndPreservesDisplayedItems(bool fails)
    {
        var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var result = new TaskCompletionSource<PagedResult<int>>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var viewModel = CreateViewModel((_, _, token) =>
        {
            started.SetResult(token);
            return result.Task;
        });
        viewModel.Items.Add(1);
        var loading = viewModel.LoadAsync(CancellationToken.None);
        var token = await started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        viewModel.ResetState();
        if (fails) result.SetException(new IOException("Late failure"));
        else result.SetResult(new PagedResult<int> { PageNumber = 1, PageSize = 50, TotalCount = 1, Items = [2] });
        await loading.WaitAsync(TimeSpan.FromSeconds(10));

        token.IsCancellationRequested.Should().BeTrue();
        viewModel.Items.Should().Equal(1);
        viewModel.HasLoadError.Should().BeFalse();
        viewModel.IsLoading.Should().BeFalse();
    }

    [Fact]
    public async Task LoadAsync_AfterFailure_ClearsErrorWhenRetrySucceeds()
    {
        var attempts = 0;
        using var viewModel = CreateViewModel((page, size, _) => ++attempts == 1
            ? Task.FromException<PagedResult<int>>(new IOException("Temporarily unavailable"))
            : Task.FromResult(new PagedResult<int> { PageNumber = page, PageSize = size, TotalCount = 1, Items = [7] }));

        await viewModel.LoadAsync(CancellationToken.None);
        viewModel.HasLoadError.Should().BeTrue();
        viewModel.IsLoading.Should().BeFalse();

        await viewModel.LoadAsync(CancellationToken.None);
        viewModel.HasLoadError.Should().BeFalse();
        viewModel.Items.Should().Equal(7);
    }

    [Fact]
    public async Task LoadAll_WaitsForUiBatchAndDiscardsItAfterReset()
    {
        var settings = Substitute.For<IUISettingsService>();
        settings.GetSongsPerPageAsync().Returns(0);
        var dispatcher = Substitute.For<IDispatcherService>();
        dispatcher.TryEnqueue(Arg.Any<Action>()).Returns(call => { call.Arg<Action>()(); return true; });
        var batchQueued = new TaskCompletionSource<Func<Task>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var batchApplied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        dispatcher.EnqueueAsync(Arg.Any<Func<Task>>()).Returns(call =>
        {
            batchQueued.TrySetResult(call.Arg<Func<Task>>());
            return batchApplied.Task;
        });
        var requestedPages = new List<int>();
        using var viewModel = new TestViewModel(settings, (page, size, _) =>
        {
            requestedPages.Add(page);
            return Task.FromResult(new PagedResult<int>
            {
                PageNumber = page, PageSize = size, TotalCount = 1500,
                Items = Enumerable.Range((page - 1) * size, size).ToArray()
            });
        }, dispatcher);

        await viewModel.LoadAsync(CancellationToken.None);
        var applyBatch = await batchQueued.Task.WaitAsync(TimeSpan.FromSeconds(5));
        requestedPages.Should().Equal(1, 2);

        viewModel.ResetState();
        await applyBatch();
        batchApplied.SetResult();

        viewModel.Items.Should().Equal(Enumerable.Range(0, 500));
    }

    [Fact]
    public async Task LoadAsync_WhenNewPageRequestedDuringLoad_DiscardsOldResultAndLoadsLatestPage()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstResult = new TaskCompletionSource<PagedResult<int>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requestedPages = new List<int>();
        using var viewModel = CreateViewModel((page, size, _) =>
        {
            requestedPages.Add(page);
            if (page == 1)
            {
                started.SetResult();
                return firstResult.Task;
            }
            return Task.FromResult(new PagedResult<int> { PageNumber = page, PageSize = size, TotalCount = 100, Items = [2] });
        });
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(viewModel.IsLoading) && !viewModel.IsLoading && viewModel.Items.Contains(2))
                finished.TrySetResult();
        };
        var loading = viewModel.LoadAsync(CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        viewModel.CurrentPage = 2;
        await viewModel.LoadAsync(CancellationToken.None);
        firstResult.SetResult(new PagedResult<int> { PageNumber = 1, PageSize = 50, TotalCount = 100, Items = [1] });

        await loading.WaitAsync(TimeSpan.FromSeconds(10));
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(10));

        requestedPages.Should().Equal(1, 2);
        viewModel.Items.Should().Equal(2);
        viewModel.CurrentPage.Should().Be(2);
    }

    [Fact]
    public async Task ChangingPageSize_WhenSavingFails_StillReloadsTheSelectedSize()
    {
        var settings = Substitute.For<IUISettingsService>();
        settings.GetSongsPerPageAsync().Returns(50);
        settings.SetSongsPerPageAsync(25).Returns(_ => throw new IOException("Settings unavailable"));
        using var viewModel = new TestViewModel(settings, (page, size, _) => Task.FromResult(
            new PagedResult<int> { PageNumber = page, PageSize = size, TotalCount = 100, Items = [size] }));
        await viewModel.LoadAsync(CancellationToken.None);
        var reloaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(viewModel.IsLoading) && !viewModel.IsLoading && viewModel.Items.Contains(25))
                reloaded.TrySetResult();
        };

        viewModel.SongsPerPage = 25;
        await reloaded.Task.WaitAsync(TimeSpan.FromSeconds(5));

        viewModel.TotalPages.Should().Be(4);
        viewModel.HasLoadError.Should().BeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NavigatePage_WhenLoadFails_KeepsPageNumberWithExistingItems(bool next)
    {
        using var viewModel = CreateViewModel((_, _, _) => Task.FromException<PagedResult<int>>(new IOException("Offline")));
        var originalPage = next ? 1 : 2;
        viewModel.ApplyResult(new PagedResult<int> { PageNumber = originalPage, PageSize = 50, TotalCount = 100, Items = [7] });

        if (next) await viewModel.NextPageAsync();
        else await viewModel.PreviousPageAsync();

        viewModel.CurrentPage.Should().Be(originalPage);
        viewModel.HasNextPage.Should().Be(next);
        viewModel.HasPreviousPage.Should().Be(!next);
        viewModel.Items.Should().Equal(7);
        viewModel.HasLoadError.Should().BeTrue();
    }

    private static TestViewModel CreateViewModel(Func<int, int, CancellationToken, Task<PagedResult<int>>> load)
    {
        var settings = Substitute.For<IUISettingsService>();
        settings.GetSongsPerPageAsync().Returns(50);
        return new TestViewModel(settings, load);
    }

    private sealed class TestViewModel(IUISettingsService settings,
        Func<int, int, CancellationToken, Task<PagedResult<int>>> load,
        IDispatcherService? dispatcher = null)
        : PagedListViewModelBase<int>(Substitute.For<ILibraryReader>(), Substitute.For<ILibraryScanner>(), settings,
            dispatcher ?? new ImmediateDispatcher(), NullLogger.Instance)
    {
        public List<int> Items { get; } = [];
        public void ApplyResult(PagedResult<int> result) => ProcessPagedResult(result, CancellationToken.None);

        protected override Task<PagedResult<int>> LoadPageItemsAsync(int pageNumber, int pageSize, CancellationToken token) =>
            load(pageNumber, pageSize, token);

        protected override string FormatTotalItemsText(int count) => count.ToString();

        protected override void ApplyItemsToCollection(PagedResult<int> result, bool append)
        {
            if (!append) Items.Clear();
            Items.AddRange(result.Items);
        }
    }

}
