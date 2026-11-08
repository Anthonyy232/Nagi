using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nagi.Core.Library;
using Nagi.Core.Playback;
using Nagi.Core.Playlists;
using Nagi.Presentation.Common;
using Nagi.Presentation.Lists;
using Nagi.Presentation.Navigation;
using Nagi.Presentation.Tests.Utils;
using NSubstitute;
using Xunit;

namespace Nagi.Presentation.Tests.Lists;

public class SmartPlaylistSongListViewModelTests
{
    private readonly LibraryFakes _library = new LibraryFakes();
    private readonly ISmartPlaylistService _playlists = Substitute.For<ISmartPlaylistService>();
    private readonly IMusicPlaybackService _playback = Substitute.For<IMusicPlaybackService>();

    public SmartPlaylistSongListViewModelTests()
    {
        _playlists.GetSmartPlaylistByIdAsync(Arg.Any<Guid>())
            .Returns(call => new SmartPlaylist { Id = call.Arg<Guid>(), Name = "Playlist" });
        _playlists.GetMatchingSongsPagedAsync(Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<int>(),
                Arg.Any<string?>(), Arg.Any<SongSortOrder?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(new PagedResult<Song>());
    }

    [Fact]
    public async Task Initialize_LateMetadata_DoesNotOverwriteNewerPlaylist()
    {
        var oldId = Guid.NewGuid();
        var pending = new TaskCompletionSource<SmartPlaylist?>();
        _playlists.GetSmartPlaylistByIdAsync(oldId).Returns(pending.Task);
        var current = new SmartPlaylist
        {
            Name = "Current",
            CoverImageUri = "https://example.com/current.jpg",
            SortOrder = SongSortOrder.DurationDesc
        };
        _playlists.GetSmartPlaylistByIdAsync(current.Id).Returns(current);
        using var viewModel = CreateViewModel();
        var oldLoad = viewModel.OpenAsync(new SmartPlaylistSongViewNavigationParameter { Title = "Old", SmartPlaylistId = oldId });
        await viewModel.OpenAsync(new SmartPlaylistSongViewNavigationParameter { Title = "Navigation label", SmartPlaylistId = current.Id });

        pending.SetResult(new SmartPlaylist { Id = oldId, Name = "Outdated" });
        await oldLoad;

        viewModel.PageTitle.Should().Be("Current");
        viewModel.CoverImageUri.Should().StartWith(current.CoverImageUri);
        viewModel.Sort.Current.Should().Be(SongSortOrder.DurationDesc);
        await _playlists.DidNotReceive().GetMatchingSongsPagedAsync(oldId, Arg.Any<int>(), Arg.Any<int>(),
            Arg.Any<string?>(), Arg.Any<SongSortOrder?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Initialize_WhenResetOrDisposed_IgnoresPendingMetadata(bool dispose)
    {
        var id = Guid.NewGuid();
        var pending = new TaskCompletionSource<SmartPlaylist?>();
        _playlists.GetSmartPlaylistByIdAsync(id).Returns(pending.Task);
        using var viewModel = CreateViewModel();
        var loading = viewModel.OpenAsync(new SmartPlaylistSongViewNavigationParameter { Title = "Initial", SmartPlaylistId = id });

        if (dispose) viewModel.Dispose();
        else viewModel.ResetState();
        pending.SetResult(new SmartPlaylist { Id = id, Name = "Outdated", SortOrder = SongSortOrder.Random });
        await loading;

        viewModel.PageTitle.Should().Be("Initial");
        viewModel.Sort.Current.Should().Be(SongSortOrder.TitleAsc);
        viewModel.Songs.Should().BeEmpty();
    }

    [Fact]
    public async Task Initialize_WhileSongLoadIsRunning_LoadsNewPlaylistAfterOldLoadFinishes()
    {
        var oldId = Guid.NewGuid();
        var newId = Guid.NewGuid();
        var started = new TaskCompletionSource();
        var pending = new TaskCompletionSource<PagedResult<Song>>();
        _playlists.GetMatchingSongsPagedAsync(oldId, Arg.Any<int>(), Arg.Any<int>(),
                Arg.Any<string?>(), Arg.Any<SongSortOrder?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(_ => { started.SetResult(); return pending.Task; });
        var currentSong = new Song { Title = "Current song" };
        _playlists.GetMatchingSongsPagedAsync(newId, Arg.Any<int>(), Arg.Any<int>(),
                Arg.Any<string?>(), Arg.Any<SongSortOrder?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(new PagedResult<Song> { Items = [currentSong], TotalCount = 1 });
        using var viewModel = CreateViewModel();
        var applied = new TaskCompletionSource();
        viewModel.Songs.CollectionChanged += (_, _) =>
        {
            if (viewModel.Songs.Contains(currentSong)) applied.TrySetResult();
        };
        var oldLoad = viewModel.OpenAsync(new SmartPlaylistSongViewNavigationParameter { Title = "Old", SmartPlaylistId = oldId });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await viewModel.OpenAsync(new SmartPlaylistSongViewNavigationParameter { Title = "New", SmartPlaylistId = newId });
        pending.SetResult(new PagedResult<Song> { Items = [new Song { Title = "Old song" }], TotalCount = 1 });
        await oldLoad;
        await applied.Task.WaitAsync(TimeSpan.FromSeconds(10));

        viewModel.Songs.Should().Equal(currentSong);
    }

    [Fact]
    public async Task RandomOrder_ShowsAndPlaysOneShuffleUntilSortedAgain()
    {
        var playlist = new SmartPlaylist { Name = "Random", SortOrder = SongSortOrder.Random };
        _playlists.GetSmartPlaylistByIdAsync(playlist.Id).Returns(playlist);
        var seeds = new List<int?>();
        _playlists.GetMatchingSongsPagedAsync(playlist.Id, Arg.Any<int>(), Arg.Any<int>(), Arg.Any<string?>(), SongSortOrder.Random,
                Arg.Do<int?>(seeds.Add), Arg.Any<CancellationToken>())
            .Returns(new PagedResult<Song> { Items = [new Song()], TotalCount = 1 });
        _playlists.GetMatchingSongIdsAsync(playlist.Id, Arg.Any<string?>(), SongSortOrder.Random, Arg.Do<int?>(seeds.Add),
                Arg.Any<CancellationToken>())
            .Returns(_ => [Guid.NewGuid()]);
        using var viewModel = CreateViewModel();

        await viewModel.OpenAsync(new SmartPlaylistSongViewNavigationParameter { Title = "Random", SmartPlaylistId = playlist.Id });
        await viewModel.PlayAllSongsCommand.ExecuteAsync(null);
        seeds.Should().HaveCount(2).And.OnlyContain(seed => seed == seeds[0]);

        await viewModel.Sort.ChangeCommand.ExecuteAsync(SongSortOrder.TitleAsc);
        await viewModel.Sort.ChangeCommand.ExecuteAsync(SongSortOrder.Random);
        await viewModel.PlayAllSongsCommand.ExecuteAsync(null);

        seeds[^1].Should().NotBe(seeds[0]);
    }

    private SmartPlaylistSongListViewModel CreateViewModel() =>
        new(_library.Reader, _library.Listening, _library.Notifier, Substitute.For<IPlaylistService>(), _playlists,
            _playback, Substitute.For<IMusicNavigationService>(), new ImmediateDispatcher(), TestSettings.Create(),
            Substitute.For<IUIService>(), NullLogger<SmartPlaylistSongListViewModel>.Instance);
}
