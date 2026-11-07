using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nagi.Core.Library;
using Nagi.Core.Playback;
using Nagi.Presentation.Services.Abstractions;
using Nagi.Presentation.ViewModels;
using NSubstitute;
using Xunit;

namespace Nagi.Presentation.Tests;

public class GenreViewModelTests
{
    [Fact]
    public async Task ChangeSortOrder_ReordersGenres()
    {
        var library = new LibraryFakes();
        library.Reader.GetGenreSummariesAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { new GenreSummary(Guid.NewGuid(), "Jazz", 1), new GenreSummary(Guid.NewGuid(), "Rock", 1) });
        using var viewModel = CreateViewModel(library);
        await viewModel.LoadAsync(CancellationToken.None);

        await viewModel.Sort.ChangeCommand.ExecuteAsync(GenreSortOrder.NameDesc);

        viewModel.Items.Select(genre => genre.Name).Should().Equal("Rock", "Jazz");
        viewModel.Sort.Current.Should().Be(GenreSortOrder.NameDesc);
    }

    [Fact]
    public async Task ReloadWhileCancelledLoadIsFinishing_LoadsTheLatestGenres()
    {
        var library = new LibraryFakes();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstResult = new TaskCompletionSource<IEnumerable<GenreSummary>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        library.Reader.GetGenreSummariesAsync(Arg.Any<CancellationToken>()).Returns(call =>
        {
            if (Interlocked.Increment(ref attempts) != 1)
                return Task.FromResult<IEnumerable<GenreSummary>>([new GenreSummary(Guid.NewGuid(), "Rock", 1)]);
            started.SetResult();
            return firstResult.Task.WaitAsync(call.Arg<CancellationToken>());
        });
        using var viewModel = CreateViewModel(library);
        using var cancellation = new CancellationTokenSource();
        var first = viewModel.LoadAsync(cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var replacement = viewModel.LoadAsync(CancellationToken.None);
        cancellation.Cancel();
        await Task.WhenAll(first, replacement).WaitAsync(TimeSpan.FromSeconds(5));

        viewModel.Items.Should().ContainSingle().Which.Name.Should().Be("Rock");
        viewModel.IsLoading.Should().BeFalse();
        viewModel.HasLoadError.Should().BeFalse();
    }

    [Fact]
    public async Task Reset_CancelsAnEventTriggeredGenreLoad()
    {
        var library = new LibraryFakes();
        var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var genres = new TaskCompletionSource<IEnumerable<GenreSummary>>(TaskCreationOptions.RunContinuationsAsynchronously);
        library.Reader.GetGenreSummariesAsync(Arg.Any<CancellationToken>()).Returns(call =>
        {
            var token = call.Arg<CancellationToken>();
            started.SetResult(token);
            return genres.Task.WaitAsync(token);
        });
        using var viewModel = CreateViewModel(library);

        library.Notifier.Changed += Raise.Event<EventHandler<LibraryChange>>(
            library.Notifier, new LibraryChange(LibraryChangeKind.LibraryRescanned));
        try
        {
            var token = await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            viewModel.ResetState();

            token.IsCancellationRequested.Should().BeTrue();
        }
        finally
        {
            genres.TrySetResult(Array.Empty<GenreSummary>());
        }
    }

    [Fact]
    public async Task FailedReload_SearchDoesNotRestoreStaleGenres()
    {
        var library = new LibraryFakes();
        library.Reader.GetGenreSummariesAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { new GenreSummary(Guid.NewGuid(), "Rock", 1) });
        using var viewModel = CreateViewModel(library);
        await viewModel.LoadAsync(CancellationToken.None);
        viewModel.Items.Should().ContainSingle();
        library.Reader.GetGenreSummariesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IEnumerable<GenreSummary>>(new IOException("Library unavailable")));

        await viewModel.LoadAsync(CancellationToken.None);
        await viewModel.SearchAsync();

        viewModel.HasLoadError.Should().BeTrue();
        viewModel.Items.Should().BeEmpty();
        viewModel.ResetState();
    }

    [Fact]
    public async Task PlayGenre_WhenPlaybackFails_TellsTheUser()
    {
        var library = new LibraryFakes();
        var playback = Substitute.For<IMusicPlaybackService>();
        playback.PlayGenreAsync(Arg.Any<Guid>()).Returns(Task.FromException(new IOException("Database locked")));
        var ui = Substitute.For<IUIService>();
        using var viewModel = CreateViewModel(library, playback, ui);

        await viewModel.PlayGenreCommand.ExecuteAsync(Guid.NewGuid());

        await ui.Received(1).ShowMessageDialogAsync(Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task CancelledLoad_StopsWaitingForGenresWithoutDisplayingAnError()
    {
        var library = new LibraryFakes();
        var pendingGenres = new TaskCompletionSource<IEnumerable<GenreSummary>>(TaskCreationOptions.RunContinuationsAsynchronously);
        library.Reader.GetGenreSummariesAsync(Arg.Any<CancellationToken>())
            .Returns(call => pendingGenres.Task.WaitAsync(call.Arg<CancellationToken>()));
        using var viewModel = CreateViewModel(library);
        using var cancellation = new CancellationTokenSource();

        var load = viewModel.LoadAsync(cancellation.Token);
        cancellation.Cancel();
        try
        {
            await load.WaitAsync(TimeSpan.FromSeconds(2));
            viewModel.IsLoading.Should().BeFalse();
            viewModel.HasLoadError.Should().BeFalse();
        }
        finally
        {
            pendingGenres.TrySetResult(Array.Empty<GenreSummary>());
            await load;
        }
    }

    private static GenreViewModel CreateViewModel(LibraryFakes library, IMusicPlaybackService? playback = null,
        IUIService? ui = null) =>
        new(library.Reader, library.Notifier, playback ?? Substitute.For<IMusicPlaybackService>(),
            Substitute.For<IMusicNavigationService>(), TestSettings.Create(),
            new ImmediateDispatcher(), ui ?? Substitute.For<IUIService>(), NullLogger<GenreViewModel>.Instance);
}
