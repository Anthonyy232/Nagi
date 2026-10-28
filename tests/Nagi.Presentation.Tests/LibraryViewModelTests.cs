using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;
using Nagi.Presentation.Services.Abstractions;
using Nagi.Presentation.ViewModels;
using NSubstitute;
using Xunit;

namespace Nagi.Presentation.Tests;

public class LibraryViewModelTests
{
    [Fact]
    public async Task FavoritesNavigation_UsesFavoriteSongsAndRestoresLibraryPlaybackOrder()
    {
        var library = new LibraryFakes();
        var settings = Substitute.For<IUISettingsService>();
        var playback = Substitute.For<IMusicPlaybackService>();
        settings.GetSongsPerPageAsync().Returns(50);
        var favorite = new Song { Id = Guid.NewGuid(), Title = "Favorite", IsLoved = true };
        var other = new Song { Id = Guid.NewGuid(), Title = "Other" };
        library.Reader.GetFavoritesPagedAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(),
            Arg.Any<SongSortOrder>(), Arg.Any<CancellationToken>()).Returns(new PagedResult<Song>
            { PageNumber = 1, PageSize = 50, TotalCount = 1, Items = [favorite] });
        library.Reader.GetAllSongsPagedAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<SongSortOrder>(),
            Arg.Any<CancellationToken>()).Returns(new PagedResult<Song>
            { PageNumber = 1, PageSize = 50, TotalCount = 2, Items = [favorite, other] });
        library.Reader.GetFavoriteSongIdsAsync(Arg.Any<string>(), Arg.Any<SongSortOrder>(), Arg.Any<CancellationToken>())
            .Returns([favorite.Id]);
        library.Reader.GetAllSongIdsAsync(Arg.Any<SongSortOrder>(), Arg.Any<CancellationToken>())
            .Returns([favorite.Id, other.Id]);
        using var viewModel = new LibraryViewModel(library.Reader, library.Writer, library.Scanner, Substitute.For<IPlaylistService>(), playback,
            Substitute.For<IMusicNavigationService>(), new ImmediateDispatcher(), settings,
            Substitute.For<IUIService>(), NullLogger<LibraryViewModel>.Instance);

        await viewModel.SetLibraryViewAsync(true);
        await viewModel.InitializeAsync();
        viewModel.Songs.Should().ContainSingle().Which.Should().Be(favorite);
        await viewModel.PlayAllSongsCommand.ExecuteAsync(null);
        await playback.Received().PlayAsync(Arg.Is<IEnumerable<Guid>>(ids => ids.SequenceEqual(new[] { favorite.Id })),
            0, null, Arg.Any<PlaybackContext>());

        await viewModel.SetLibraryViewAsync(false);
        viewModel.Songs.Should().Equal(favorite, other);
        await viewModel.PlayAllSongsCommand.ExecuteAsync(null);
        await playback.Received().PlayAsync(Arg.Is<IEnumerable<Guid>>(ids => ids.SequenceEqual(new[] { favorite.Id, other.Id })),
            0, null, Arg.Any<PlaybackContext>());
    }

    [Fact]
    public async Task ArticleSortSetting_RefreshesDisplayedSongsAndPlaybackOrder()
    {
        var library = new LibraryFakes();
        var settings = Substitute.For<IUISettingsService>();
        var playback = Substitute.For<IMusicPlaybackService>();
        settings.GetSongsPerPageAsync().Returns(50);
        var apple = new Song { Id = Guid.NewGuid(), Title = "Apple" };
        var moon = new Song { Id = Guid.NewGuid(), Title = "A Moon" };
        Song[] orderedSongs = [apple, moon];
        library.Reader.GetAllSongsPagedAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<SongSortOrder>(),
            Arg.Any<CancellationToken>()).Returns(_ => new PagedResult<Song>
            {
                PageNumber = 1,
                PageSize = 50,
                TotalCount = 2,
                Items = orderedSongs
            });
        library.Reader.GetAllSongIdsAsync(Arg.Any<SongSortOrder>(), Arg.Any<CancellationToken>())
            .Returns(_ => orderedSongs.Select(song => song.Id).ToList());
        using var viewModel = new LibraryViewModel(library.Reader, library.Writer, library.Scanner, Substitute.For<IPlaylistService>(), playback,
            Substitute.For<IMusicNavigationService>(), new ImmediateDispatcher(), settings,
            Substitute.For<IUIService>(), NullLogger<LibraryViewModel>.Instance);
        await viewModel.RefreshOrSortSongsAsync();
        viewModel.Songs.Select(song => song.Title).Should().Equal("Apple", "A Moon");
        await viewModel.PlayAllSongsCommand.ExecuteAsync(null);
        playback.ClearReceivedCalls();

        var refreshed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(viewModel.IsLoading) && !viewModel.IsLoading)
                refreshed.TrySetResult();
        };
        orderedSongs = [moon, apple];
        settings.IgnoreLeadingArticlesOnSortEnabledChanged += Raise.Event<Action<bool>>(false);
        await refreshed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        viewModel.Songs.Select(song => song.Title).Should().Equal("A Moon", "Apple");
        await viewModel.PlayAllSongsCommand.ExecuteAsync(null);
        await playback.Received().PlayAsync(Arg.Is<IEnumerable<Guid>>(ids => ids.SequenceEqual(new[] { moon.Id, apple.Id })),
            0, null, Arg.Any<PlaybackContext>());
    }

    [Fact]
    public async Task Initialize_AfterQueryFailure_CanRetry()
    {
        var library = new LibraryFakes();
        var settings = Substitute.For<IUISettingsService>();
        settings.GetSongsPerPageAsync().Returns(50);
        var attempts = 0;
        library.Reader.GetAllSongsPagedAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<SongSortOrder>(),
            Arg.Any<CancellationToken>()).Returns(_ => attempts++ == 0
                ? Task.FromException<PagedResult<Song>>(new IOException("Library unavailable"))
                : Task.FromResult(new PagedResult<Song>
                {
                    PageNumber = 1,
                    PageSize = 50,
                    TotalCount = 1,
                    Items = [new Song { Title = "Recovered" }]
                }));
        var scanFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        library.Scanner.RefreshAllFoldersAsync(Arg.Any<IProgress<ScanProgress>>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            scanFinished.TrySetResult();
            return Task.FromResult(true);
        });
        using var viewModel = new LibraryViewModel(library.Reader, library.Writer, library.Scanner, Substitute.For<IPlaylistService>(),
            Substitute.For<IMusicPlaybackService>(),
            Substitute.For<IMusicNavigationService>(), new ImmediateDispatcher(), settings,
            Substitute.For<IUIService>(), NullLogger<LibraryViewModel>.Instance);

        await viewModel.InitializeAsync();
        viewModel.HasInitialized.Should().BeFalse();

        await viewModel.InitializeAsync();
        await scanFinished.Task.WaitAsync(TimeSpan.FromSeconds(5));

        viewModel.HasInitialized.Should().BeTrue();
        viewModel.Songs.Should().ContainSingle().Which.Title.Should().Be("Recovered");
        await library.Scanner.Received(1).RefreshAllFoldersAsync(Arg.Any<IProgress<ScanProgress>>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LibraryRefresh_RespectsResetAfterReachingTheUiQueue(bool reset)
    {
        var library = new LibraryFakes();
        var settings = Substitute.For<IUISettingsService>();
        settings.GetSongsPerPageAsync().Returns(50);
        library.Reader.GetAllSongsPagedAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<SongSortOrder>(),
            Arg.Any<CancellationToken>()).Returns(new PagedResult<Song>
            {
                PageNumber = 1,
                PageSize = 50,
                TotalCount = 1,
                Items = [new Song { Title = "New song" }]
            });
        var dispatcher = new HeldDispatcher();
        using var viewModel = new LibraryViewModel(library.Reader, library.Writer, library.Scanner, Substitute.For<IPlaylistService>(),
            Substitute.For<IMusicPlaybackService>(),
            Substitute.For<IMusicNavigationService>(), dispatcher, settings, Substitute.For<IUIService>(),
            NullLogger<LibraryViewModel>.Instance);

        library.Scanner.LibraryContentChanged += Raise.Event<EventHandler<LibraryContentChangedEventArgs>>(
            library.Scanner, new LibraryContentChangedEventArgs(LibraryChangeType.LibraryRescanned));
        var update = await dispatcher.Queued.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (reset) viewModel.ResetState();
        await update();

        if (reset)
        {
            await library.Reader.DidNotReceive().GetAllSongsPagedAsync(Arg.Any<int>(), Arg.Any<int>(),
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
