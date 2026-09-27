using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nagi.Core.Helpers;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;
using Nagi.WinUI.Services.Abstractions;
using Nagi.WinUI.ViewModels;
using NSubstitute;
using Xunit;

namespace Nagi.WinUI.Tests;

public class LibraryViewModelTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Initialize_AfterSettingsOrQueryFailure_CanRetry(bool settingsFailure)
    {
        var library = Substitute.For<ILibraryService>();
        var settings = Substitute.For<IUISettingsService>();
        settings.GetSongsPerPageAsync().Returns(50);
        var attempts = 0;
        settings.GetSortOrderAsync<SongSortOrder>(SortOrderHelper.LibrarySortOrderKey)
            .Returns(_ => settingsFailure && attempts++ == 0
                ? Task.FromException<SongSortOrder>(new IOException("Settings unavailable"))
                : Task.FromResult(SongSortOrder.TitleAsc));
        library.GetAllSongsPagedAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<SongSortOrder>(),
            Arg.Any<CancellationToken>()).Returns(_ => !settingsFailure && attempts++ == 0
                ? Task.FromException<PagedResult<Song>>(new IOException("Library unavailable"))
                : Task.FromResult(new PagedResult<Song>
                {
                    PageNumber = 1, PageSize = 50, TotalCount = 1, Items = [new Song { Title = "Recovered" }]
                }));
        var scanFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        library.RefreshAllFoldersAsync(Arg.Any<IProgress<ScanProgress>>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            scanFinished.TrySetResult();
            return Task.FromResult(true);
        });
        using var viewModel = new LibraryViewModel(library, Substitute.For<IPlaylistService>(),
            Substitute.For<IMusicPlaybackService>(),
            Substitute.For<IMusicNavigationService>(), new ImmediateDispatcher(), settings,
            Substitute.For<IUIService>(), NullLogger<LibraryViewModel>.Instance);

        if (settingsFailure)
            await Assert.ThrowsAsync<IOException>(() => viewModel.InitializeAsync());
        else
            await viewModel.InitializeAsync();
        viewModel.HasInitialized.Should().BeFalse();

        await viewModel.InitializeAsync();
        await scanFinished.Task.WaitAsync(TimeSpan.FromSeconds(5));

        viewModel.HasInitialized.Should().BeTrue();
        viewModel.Songs.Should().ContainSingle().Which.Title.Should().Be("Recovered");
        await library.Received(1).RefreshAllFoldersAsync(Arg.Any<IProgress<ScanProgress>>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LibraryRefresh_RespectsResetAfterReachingTheUiQueue(bool reset)
    {
        var library = Substitute.For<ILibraryService>();
        var settings = Substitute.For<IUISettingsService>();
        settings.GetSongsPerPageAsync().Returns(50);
        library.GetAllSongsPagedAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<SongSortOrder>(),
            Arg.Any<CancellationToken>()).Returns(new PagedResult<Song>
            {
                PageNumber = 1, PageSize = 50, TotalCount = 1, Items = [new Song { Title = "New song" }]
            });
        var dispatcher = new HeldDispatcher();
        using var viewModel = new LibraryViewModel(library, Substitute.For<IPlaylistService>(),
            Substitute.For<IMusicPlaybackService>(),
            Substitute.For<IMusicNavigationService>(), dispatcher, settings, Substitute.For<IUIService>(),
            NullLogger<LibraryViewModel>.Instance);

        library.LibraryContentChanged += Raise.Event<EventHandler<LibraryContentChangedEventArgs>>(
            library, new LibraryContentChangedEventArgs(LibraryChangeType.LibraryRescanned));
        var update = await dispatcher.Queued.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (reset) viewModel.ResetState();
        await update();

        if (reset)
        {
            await library.DidNotReceive().GetAllSongsPagedAsync(Arg.Any<int>(), Arg.Any<int>(),
                Arg.Any<SongSortOrder>(), Arg.Any<CancellationToken>());
            viewModel.Songs.Should().BeEmpty();
        }
        else
        {
            viewModel.Songs.Should().ContainSingle().Which.Title.Should().Be("New song");
        }
    }

    private sealed class HeldDispatcher : IDispatcherService
    {
        public TaskCompletionSource<Func<Task>> Queued { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool HasThreadAccess => true;
        public bool TryEnqueue(Action action) { action(); return true; }
        public Task EnqueueAsync(Func<Task> function)
        {
            Queued.TrySetResult(function);
            return Task.CompletedTask;
        }
        public Task<T> EnqueueAsync<T>(Func<T> function) => Task.FromResult(function());
    }
}
