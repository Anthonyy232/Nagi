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

public class AlbumViewViewModelTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AlbumLookupCompletesAfterDisposal_DoesNotUpdateTheViewOrLoadSongs(bool fails)
    {
        var library = Substitute.For<ILibraryService>();
        var result = new TaskCompletionSource<Album?>(TaskCreationOptions.RunContinuationsAsynchronously);
        library.GetAlbumByIdAsync(Arg.Any<Guid>()).Returns(result.Task);
        using var viewModel = CreateViewModel(library);
        var title = viewModel.AlbumTitle;
        var load = viewModel.LoadAlbumDetailsAsync(Guid.NewGuid());

        viewModel.Dispose();
        if (fails) result.SetException(new IOException("Old lookup failed"));
        else result.SetResult(new Album { Title = "Old album" });
        await load.WaitAsync(TimeSpan.FromSeconds(5));

        viewModel.AlbumTitle.Should().Be(title);
        await library.DidNotReceive().GetSongsByAlbumIdPagedAsync(Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<int>(),
            Arg.Any<SongSortOrder>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MissingAlbum_DoesNotStartAnOrphanedSongQuery()
    {
        var library = Substitute.For<ILibraryService>();
        using var viewModel = CreateViewModel(library);

        await viewModel.LoadAlbumDetailsAsync(Guid.NewGuid());

        viewModel.Songs.Should().BeEmpty();
        viewModel.GroupedSongsFlat.Should().BeEmpty();
        await library.DidNotReceive().GetSongsByAlbumIdPagedAsync(Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<int>(),
            Arg.Any<SongSortOrder>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DurationResultQueuedBeforeReset_DoesNotOverwriteTheNextAlbum()
    {
        var library = Substitute.For<ILibraryService>();
        library.GetAlbumByIdAsync(Arg.Any<Guid>()).Returns(call => new Album { Id = call.Arg<Guid>(), Title = "Album" });
        library.GetSongsByAlbumIdPagedAsync(Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<int>(),
            Arg.Any<SongSortOrder>(), Arg.Any<CancellationToken>()).Returns(new PagedResult<Song>
            {
                PageNumber = 1, PageSize = 50, TotalCount = 51, Items = [new Song { DurationTicks = TimeSpan.TicksPerMinute }]
            });
        var duration = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
        library.GetSearchTotalDurationInAlbumAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(duration.Task);
        var dispatcher = new HoldingDispatcher();
        using var viewModel = CreateViewModel(library, dispatcher);
        await viewModel.LoadAlbumDetailsAsync(Guid.NewGuid());
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
        var library = Substitute.For<ILibraryService>();
        library.GetAlbumByIdAsync(Arg.Any<Guid>()).Returns(new Album { Title = "Long album" });
        library.GetSongsByAlbumIdPagedAsync(Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<int>(),
            Arg.Any<SongSortOrder>(), Arg.Any<CancellationToken>()).Returns(new PagedResult<Song>
            {
                PageNumber = 1, PageSize = 50, TotalCount = 1,
                Items = [new Song { DurationTicks = TimeSpan.FromHours(25).Ticks }]
            });
        using var viewModel = CreateViewModel(library);

        await viewModel.LoadAlbumDetailsAsync(Guid.NewGuid());

        viewModel.AlbumDetailsText.Should().EndWith("25:00:00");
    }

    private static AlbumViewViewModel CreateViewModel(ILibraryService library, IDispatcherService? dispatcher = null)
    {
        var settings = Substitute.For<IUISettingsService>();
        settings.GetSongsPerPageAsync().Returns(50);
        return new AlbumViewViewModel(library, library, library, Substitute.For<IPlaylistService>(),
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
