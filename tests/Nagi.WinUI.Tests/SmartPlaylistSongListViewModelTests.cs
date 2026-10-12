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

public class SmartPlaylistSongListViewModelTests
{
    private readonly LibraryFakes _library = new LibraryFakes();
    private readonly ISmartPlaylistService _playlists = Substitute.For<ISmartPlaylistService>();

    public SmartPlaylistSongListViewModelTests()
    {
        _playlists.GetSmartPlaylistByIdAsync(Arg.Any<Guid>())
            .Returns(call => new SmartPlaylist { Id = call.Arg<Guid>(), Name = "Playlist" });
        _playlists.GetMatchingSongsPagedAsync(Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<int>(),
                Arg.Any<string?>(), Arg.Any<SongSortOrder?>(), Arg.Any<CancellationToken>())
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
            Name = "Current", CoverImageUri = "https://example.com/current.jpg",
            SortOrder = SmartPlaylistSortOrder.DurationDesc
        };
        _playlists.GetSmartPlaylistByIdAsync(current.Id).Returns(current);
        using var viewModel = CreateViewModel();
        var oldLoad = viewModel.InitializeAsync("Old", oldId);
        await viewModel.InitializeAsync("Navigation label", current.Id);

        pending.SetResult(new SmartPlaylist { Id = oldId, Name = "Outdated" });
        await oldLoad;

        viewModel.GetSmartPlaylistId().Should().Be(current.Id);
        viewModel.PageTitle.Should().Be("Current");
        viewModel.CoverImageUri.Should().StartWith(current.CoverImageUri);
        viewModel.CurrentSortOrder.Should().Be(SongSortOrder.DurationDesc);
        await _playlists.DidNotReceive().GetMatchingSongsPagedAsync(oldId, Arg.Any<int>(), Arg.Any<int>(),
            Arg.Any<string?>(), Arg.Any<SongSortOrder?>(), Arg.Any<CancellationToken>());
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
        var loading = viewModel.InitializeAsync("Initial", id);

        if (dispose) viewModel.Dispose();
        else viewModel.ResetState();
        pending.SetResult(new SmartPlaylist { Id = id, Name = "Outdated", SortOrder = SmartPlaylistSortOrder.Random });
        await loading;

        viewModel.GetSmartPlaylistId().Should().BeNull();
        viewModel.PageTitle.Should().Be("Initial");
        viewModel.CurrentSortOrder.Should().Be(SongSortOrder.TitleAsc);
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
                Arg.Any<string?>(), Arg.Any<SongSortOrder?>(), Arg.Any<CancellationToken>())
            .Returns(_ => { started.SetResult(); return pending.Task; });
        var currentSong = new Song { Title = "Current song" };
        _playlists.GetMatchingSongsPagedAsync(newId, Arg.Any<int>(), Arg.Any<int>(),
                Arg.Any<string?>(), Arg.Any<SongSortOrder?>(), Arg.Any<CancellationToken>())
            .Returns(new PagedResult<Song> { Items = [currentSong], TotalCount = 1 });
        using var viewModel = CreateViewModel();
        var applied = new TaskCompletionSource();
        viewModel.Songs.CollectionChanged += (_, _) =>
        {
            if (viewModel.Songs.Contains(currentSong)) applied.TrySetResult();
        };
        var oldLoad = viewModel.InitializeAsync("Old", oldId);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await viewModel.InitializeAsync("New", newId);
        pending.SetResult(new PagedResult<Song> { Items = [new Song { Title = "Old song" }], TotalCount = 1 });
        await oldLoad;
        await applied.Task.WaitAsync(TimeSpan.FromSeconds(10));

        viewModel.GetSmartPlaylistId().Should().Be(newId);
        viewModel.Songs.Should().Equal(currentSong);
    }

    [Fact]
    public async Task RandomPages_ShareOrderWithPlaybackIdsAfterOneWaiterCancels()
    {
        var id = Guid.NewGuid();
        var songs = new[] { new Song(), new Song(), new Song() };
        var ids = songs.Select(song => song.Id).ToList();
        var pending = new TaskCompletionSource<List<Guid>>();
        _playlists.GetMatchingSongIdsAsync(id, null, SongSortOrder.Random, CancellationToken.None).Returns(pending.Task);
        _library.Reader.GetSongsByIdsAsync(Arg.Any<IEnumerable<Guid>>())
            .Returns(call => (IReadOnlyDictionary<Guid, Song>)songs
                .Where(song => call.Arg<IEnumerable<Guid>>().Contains(song.Id)).ToDictionary(song => song.Id));
        using var viewModel = CreateViewModel();
        await viewModel.InitializeAsync("Random", id);
        using var cancellation = new CancellationTokenSource();
        var cancelledPage = viewModel.RandomPageAsync(1, 2, cancellation.Token);
        var playbackIds = viewModel.RandomIdsAsync();

        cancellation.Cancel();
        await ((Func<Task>)(() => cancelledPage)).Should().ThrowAsync<OperationCanceledException>();
        pending.SetResult(ids);
        (await playbackIds).Should().Equal(ids);
        (await viewModel.RandomPageAsync(1, 2)).Items.Should().Equal(songs.Take(2));
        (await viewModel.RandomPageAsync(2, 2)).Items.Should().Equal(songs.Skip(2));
        await _playlists.Received(1).GetMatchingSongIdsAsync(id, null, SongSortOrder.Random, CancellationToken.None);
    }

    private TestSmartPlaylist CreateViewModel() => new(_library, _playlists);

    private sealed class TestSmartPlaylist(LibraryFakes library, ISmartPlaylistService playlists)
        : SmartPlaylistSongListViewModel(library.Reader, library.Writer, library.Scanner, Substitute.For<IPlaylistService>(), playlists,
            Substitute.For<IMusicPlaybackService>(), Substitute.For<IMusicNavigationService>(),
            new ImmediateDispatcher(), Substitute.For<IUISettingsService>(), Substitute.For<IUIService>(),
            NullLogger<SmartPlaylistSongListViewModel>.Instance)
    {
        public Task<PagedResult<Song>> RandomPageAsync(int page, int size, CancellationToken token = default) =>
            LoadSongsPagedAsync(page, size, SongSortOrder.Random, token);

        public Task<List<Guid>> RandomIdsAsync() => LoadAllSongIdsAsync(SongSortOrder.Random);
    }
}
