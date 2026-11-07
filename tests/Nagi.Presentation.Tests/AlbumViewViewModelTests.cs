using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nagi.Core.Library;
using Nagi.Core.Playback;
using Nagi.Core.Services.Abstractions;
using Nagi.Presentation.Navigation;
using Nagi.Presentation.Services.Abstractions;
using Nagi.Presentation.ViewModels;
using NSubstitute;
using Xunit;

namespace Nagi.Presentation.Tests;

public class AlbumViewViewModelTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AlbumLookupCompletesAfterDisposal_DoesNotUpdateTheViewOrLoadSongs(bool fails)
    {
        var library = new LibraryFakes();
        var result = new TaskCompletionSource<Album?>(TaskCreationOptions.RunContinuationsAsynchronously);
        library.Reader.GetAlbumByIdAsync(Arg.Any<Guid>()).Returns(result.Task);
        using var viewModel = CreateViewModel(library);
        var title = viewModel.PageTitle;
        var load = viewModel.OpenAsync(new AlbumViewNavigationParameter(Guid.NewGuid()));

        viewModel.Dispose();
        if (fails) result.SetException(new IOException("Old lookup failed"));
        else result.SetResult(new Album { Title = "Old album" });
        await load.WaitAsync(TimeSpan.FromSeconds(5));

        viewModel.PageTitle.Should().Be(title);
        await library.Reader.DidNotReceive().GetSongsAsync(Arg.Any<SongScope>(), Arg.Any<string?>(), Arg.Any<SongSortOrder>(),
            Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MissingAlbum_DoesNotStartAnOrphanedSongQuery()
    {
        var library = new LibraryFakes();
        using var viewModel = CreateViewModel(library);

        await viewModel.OpenAsync(new AlbumViewNavigationParameter(Guid.NewGuid()));

        viewModel.Songs.Should().BeEmpty();
        viewModel.GroupedSongsFlat.Should().BeEmpty();
        await library.Reader.DidNotReceive().GetSongsAsync(Arg.Any<SongScope>(), Arg.Any<string?>(), Arg.Any<SongSortOrder>(),
            Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DurationResultQueuedBeforeReset_DoesNotOverwriteTheNextAlbum()
    {
        var library = new LibraryFakes();
        library.Reader.GetAlbumByIdAsync(Arg.Any<Guid>()).Returns(call => new Album { Id = call.Arg<Guid>(), Title = "Album" });
        library.Reader.GetSongsAsync(Arg.Any<SongScope>(), Arg.Any<string?>(), Arg.Any<SongSortOrder>(),
            Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(new PagedResult<Song>
            {
                PageNumber = 1,
                PageSize = 50,
                TotalCount = 51,
                Items = [new Song { DurationTicks = TimeSpan.TicksPerMinute }]
            });
        var duration = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
        library.Reader.GetSongsDurationAsync(Arg.Any<SongScope>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(duration.Task);
        var dispatcher = new HoldingDispatcher();
        using var viewModel = CreateViewModel(library, dispatcher);
        await viewModel.OpenAsync(new AlbumViewNavigationParameter(Guid.NewGuid()));
        var originalDetails = viewModel.AlbumDetailsText;
        dispatcher.Hold = true;

        duration.SetResult(TimeSpan.FromHours(9));
        var update = await dispatcher.Queued.Task.WaitAsync(TimeSpan.FromSeconds(5));
        viewModel.ResetState();
        update();

        viewModel.AlbumDetailsText.Should().Be(originalDetails);
    }

    [Fact]
    public async Task AlbumLongerThanOneDay_ShowsTotalHours()
    {
        var library = new LibraryFakes();
        library.Reader.GetAlbumByIdAsync(Arg.Any<Guid>()).Returns(new Album { Title = "Long album" });
        library.Reader.GetSongsAsync(Arg.Any<SongScope>(), Arg.Any<string?>(), Arg.Any<SongSortOrder>(),
            Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(new PagedResult<Song>
            {
                PageNumber = 1,
                PageSize = 50,
                TotalCount = 1,
                Items = [new Song { DurationTicks = TimeSpan.FromHours(25).Ticks }]
            });
        using var viewModel = CreateViewModel(library);

        await viewModel.OpenAsync(new AlbumViewNavigationParameter(Guid.NewGuid()));

        viewModel.AlbumDetailsText.Should().EndWith("25:00:00");
    }

    private static AlbumViewViewModel CreateViewModel(LibraryFakes library, IDispatcherService? dispatcher = null)
    {
        var settings = TestSettings.Create();
        return new AlbumViewViewModel(library.Reader, library.Listening, library.Notifier, Substitute.For<IPlaylistService>(),
            Substitute.For<IMusicPlaybackService>(),
            Substitute.For<IMusicNavigationService>(), dispatcher ?? new ImmediateDispatcher(), settings,
            Substitute.For<IUIService>(), NullLogger<AlbumViewViewModel>.Instance);
    }

    private sealed class HoldingDispatcher : IDispatcherService
    {
        public bool Hold { get; set; }
        public TaskCompletionSource<Action> Queued { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool HasThreadAccess => true;
        public bool TryEnqueue(Action action)
        {
            if (Hold) Queued.TrySetResult(action);
            else action();
            return true;
        }
        public Task EnqueueAsync(Func<Task> function) => function();
        public Task<T> EnqueueAsync<T>(Func<T> function) => Task.FromResult(function());
    }
}
