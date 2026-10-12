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

public class PlaylistSongListViewModelTests
{
    private readonly ILibraryService _library = Substitute.For<ILibraryService>();
    private readonly IPlaylistService _playlists = Substitute.For<IPlaylistService>();
    private readonly IUIService _ui = Substitute.For<IUIService>();
    private readonly Guid _playlistId = Guid.NewGuid();
    private readonly List<Song> _songs = Enumerable.Range(1, 5)
        .Select(i => new Song { Title = $"Track {i}", Order = i }).ToList();

    public PlaylistSongListViewModelTests()
    {
        _library.GetSongsByPlaylistPagedAsync(Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<int>(),
                Arg.Any<SongSortOrder>(), Arg.Any<CancellationToken>())
            .Returns(call => new PagedResult<Song>
            {
                PageNumber = 1, PageSize = call.ArgAt<int>(2), TotalCount = _songs.Count, Items = _songs.ToList()
            });
        _library.GetAllSongIdsByPlaylistIdAsync(Arg.Any<Guid>(), Arg.Any<SongSortOrder>(), Arg.Any<CancellationToken>())
            .Returns(_ => _songs.Select(song => song.Id).ToList());
        _playlists.MovePlaylistSongAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<double>()).Returns(true);
        _playlists.UpdatePlaylistOrderAsync(Arg.Any<Guid>(), Arg.Any<IEnumerable<Guid>>()).Returns(true);
    }

    [Fact]
    public async Task Reorder_WithRemoveAndAddEvents_KeepsEveryTrackInPlaybackOrder()
    {
        using var viewModel = CreateViewModel();
        await viewModel.InitializeAsync("Playlist", _playlistId);
        var moved = viewModel.Songs[0];
        viewModel.Songs.RemoveAt(0);
        viewModel.Songs.Insert(3, moved);

        await viewModel.OnSongsReorderedAsync([moved]);

        (await viewModel.GetPlaybackIdsAsync()).Should().Equal(viewModel.Songs.Select(song => song.Id));
        await _playlists.Received(1).MovePlaylistSongAsync(_playlistId, moved.Id, 4.5);
    }

    [Fact]
    public async Task Reorder_WhenSaveFails_ReloadsPersistedOrderAndReportsFailure()
    {
        _playlists.MovePlaylistSongAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<double>()).Returns(false);
        using var viewModel = CreateViewModel();
        await viewModel.InitializeAsync("Playlist", _playlistId);
        var moved = viewModel.Songs[0];
        viewModel.Songs.RemoveAt(0);
        viewModel.Songs.Insert(3, moved);

        await viewModel.OnSongsReorderedAsync([moved]);

        viewModel.Songs.Select(song => song.Id).Should().Equal(_songs.Select(song => song.Id));
        (await viewModel.GetPlaybackIdsAsync()).Should().Equal(_songs.Select(song => song.Id));
        await _ui.Received(1).ShowMessageDialogAsync(Arg.Any<string>(), Arg.Any<string>());
    }

    private TestPlaylist CreateViewModel() => new(_library, _playlists, _ui);

    [Fact]
    public async Task SelectingASong_EnablesTheRemoveCommandAndNotifiesItsButton()
    {
        using var viewModel = CreateViewModel();
        await viewModel.InitializeAsync("Playlist", _playlistId);
        var notified = false;
        viewModel.RemoveSelectedSongsFromPlaylistCommand.CanExecuteChanged += (_, _) => notified = true;

        viewModel.SelectionState.Select(_songs[0].Id);
        viewModel.OnSongsSelectionChanged([_songs[0]]);

        notified.Should().BeTrue();
        viewModel.RemoveSelectedSongsFromPlaylistCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public async Task Reorder_WithMultipleTracks_SavesCompleteOrderAndUpdatesPositions()
    {
        using var viewModel = CreateViewModel();
        await viewModel.InitializeAsync("Playlist", _playlistId);
        var moved = viewModel.Songs.Take(2).ToArray();
        foreach (var song in moved) viewModel.Songs.Remove(song);
        foreach (var song in moved) viewModel.Songs.Add(song);
        var expected = viewModel.Songs.Select(song => song.Id).ToArray();

        await viewModel.OnSongsReorderedAsync(moved);

        await _playlists.Received(1).UpdatePlaylistOrderAsync(_playlistId,
            Arg.Is<IEnumerable<Guid>>(ids => ids.SequenceEqual(expected)));
        await _playlists.DidNotReceiveWithAnyArgs().MovePlaylistSongAsync(default, default, default);
        (await viewModel.GetPlaybackIdsAsync()).Should().Equal(expected);
        viewModel.Songs.Select(song => song.Order).Should().Equal(1, 2, 3, 4, 5);
    }

    [Fact]
    public async Task Reorder_WhenPositionsAreTooClose_NormalizesBeforeAnotherMove()
    {
        _songs[1].Order = 2;
        _songs[2].Order = 2 + 1e-12;
        using var viewModel = CreateViewModel();
        await viewModel.InitializeAsync("Playlist", _playlistId);
        var moved = viewModel.Songs[0];
        viewModel.Songs.RemoveAt(0);
        viewModel.Songs.Insert(1, moved);

        await viewModel.OnSongsReorderedAsync([moved]);

        await _playlists.Received(1).UpdatePlaylistOrderAsync(_playlistId, Arg.Any<IEnumerable<Guid>>());
        viewModel.Songs.Select(song => song.Order).Should().Equal(1, 2, 3, 4, 5);

        var nextMove = viewModel.Songs[4];
        viewModel.Songs.RemoveAt(4);
        viewModel.Songs.Insert(1, nextMove);
        await viewModel.OnSongsReorderedAsync([nextMove]);

        await _playlists.Received(1).MovePlaylistSongAsync(_playlistId, nextMove.Id, 1.5);
        (await viewModel.GetPlaybackIdsAsync()).Should().Equal(viewModel.Songs.Select(song => song.Id));
    }

    [Fact]
    public async Task Reorder_WhileSaving_DisablesAnotherDragUntilComplete()
    {
        var save = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _playlists.MovePlaylistSongAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<double>()).Returns(save.Task);
        using var viewModel = CreateViewModel();
        await viewModel.InitializeAsync("Playlist", _playlistId);
        var moved = viewModel.Songs[0];
        viewModel.Songs.RemoveAt(0);
        viewModel.Songs.Insert(3, moved);

        var saving = viewModel.OnSongsReorderedAsync([moved]);
        viewModel.IsReorderingEnabled.Should().BeFalse();
        await viewModel.OnSongsReorderedAsync([moved]);
        save.SetResult(true);
        await saving.WaitAsync(TimeSpan.FromSeconds(10));

        viewModel.IsReorderingEnabled.Should().BeTrue();
        await _playlists.Received(1).MovePlaylistSongAsync(_playlistId, moved.Id, Arg.Any<double>());
    }

    [Fact]
    public async Task Reorder_WhenNavigationChangesBeforeFailure_DoesNotReloadNewPlaylistOrShowStaleError()
    {
        var save = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _playlists.MovePlaylistSongAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<double>()).Returns(save.Task);
        using var viewModel = CreateViewModel();
        await viewModel.InitializeAsync("Playlist", _playlistId);
        var moved = viewModel.Songs[0];
        viewModel.Songs.RemoveAt(0);
        viewModel.Songs.Insert(3, moved);
        var saving = viewModel.OnSongsReorderedAsync([moved]);

        viewModel.ResetState();
        var newPlaylist = Guid.NewGuid();
        await viewModel.InitializeAsync("New playlist", newPlaylist);
        save.SetResult(false);
        await saving.WaitAsync(TimeSpan.FromSeconds(10));

        viewModel.Songs.Select(song => song.Id).Should().Equal(_songs.Select(song => song.Id));
        await _ui.DidNotReceiveWithAnyArgs().ShowMessageDialogAsync(default!, default!);
        await _library.Received(1).GetSongsByPlaylistPagedAsync(newPlaylist, Arg.Any<int>(), Arg.Any<int>(),
            Arg.Any<SongSortOrder>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Reorder_WhenPlaylistStillLoading_DoesNotSavePartialOrder()
    {
        using var viewModel = CreateViewModel();
        await viewModel.InitializeAsync("Playlist", _playlistId);
        viewModel.TotalItemCount = 20;

        viewModel.IsReorderingEnabled.Should().BeFalse();
        await viewModel.OnSongsReorderedAsync(viewModel.Songs.Take(2).ToArray());

        await _playlists.DidNotReceiveWithAnyArgs().UpdatePlaylistOrderAsync(default, default!);
        await _playlists.DidNotReceiveWithAnyArgs().MovePlaylistSongAsync(default, default, default);
    }

    [Fact]
    public async Task Reorder_WithoutAChange_DoesNotWriteOrReportAnError()
    {
        using var viewModel = CreateViewModel();
        await viewModel.InitializeAsync("Playlist", _playlistId);

        await viewModel.OnSongsReorderedAsync(viewModel.Songs.Take(2).ToArray());

        await _playlists.DidNotReceiveWithAnyArgs().UpdatePlaylistOrderAsync(default, default!);
        await _ui.DidNotReceiveWithAnyArgs().ShowMessageDialogAsync(default!, default!);
    }

    private sealed class TestPlaylist(ILibraryService library, IPlaylistService playlists, IUIService ui)
        : PlaylistSongListViewModel(library, library, library, playlists, Substitute.For<IMusicPlaybackService>(),
            Substitute.For<IMusicNavigationService>(), new ImmediateDispatcher(),
            Substitute.For<IUISettingsService>(), ui, NullLogger<PlaylistSongListViewModel>.Instance)
    {
        public Task<List<Guid>>? PendingSelection { get; set; }
        protected override Task<List<Guid>> GetCurrentSelectionIdsAsync() => PendingSelection ?? base.GetCurrentSelectionIdsAsync();
        public Task<List<Guid>> GetPlaybackIdsAsync() => GetFullSongIdsAsync();
    }

    [Fact]
    public async Task RemoveSelected_WhenNavigationChangesDuringResolution_DoesNotRemoveFromNewPlaylist()
    {
        using var viewModel = CreateViewModel();
        await viewModel.InitializeAsync("Original playlist", _playlistId);
        viewModel.SelectionState.Select(_songs[0].Id);
        var pending = new TaskCompletionSource<List<Guid>>(TaskCreationOptions.RunContinuationsAsynchronously);
        viewModel.PendingSelection = pending.Task;
        var removing = viewModel.RemoveSelectedSongsFromPlaylistCommand.ExecuteAsync(null);

        viewModel.ResetState();
        await viewModel.InitializeAsync("New playlist", Guid.NewGuid());
        pending.SetResult(new List<Guid> { _songs[0].Id });
        await removing.WaitAsync(TimeSpan.FromSeconds(10));

        await _playlists.DidNotReceiveWithAnyArgs().RemoveSongsFromPlaylistAsync(default, default!);
    }

    [Fact]
    public async Task RemoveSelected_WhenNavigationChangesDuringSave_DoesNotReloadNewPlaylist()
    {
        var save = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _playlists.RemoveSongsFromPlaylistAsync(Arg.Any<Guid>(), Arg.Any<IEnumerable<Guid>>()).Returns(save.Task);
        using var viewModel = CreateViewModel();
        await viewModel.InitializeAsync("Original playlist", _playlistId);
        viewModel.SelectionState.Select(_songs[0].Id);
        var removing = viewModel.RemoveSelectedSongsFromPlaylistCommand.ExecuteAsync(null);

        viewModel.ResetState();
        var newPlaylist = Guid.NewGuid();
        await viewModel.InitializeAsync("New playlist", newPlaylist);
        save.SetResult(true);
        await removing.WaitAsync(TimeSpan.FromSeconds(10));

        await _playlists.Received(1).RemoveSongsFromPlaylistAsync(_playlistId,
            Arg.Is<IEnumerable<Guid>>(ids => ids.SequenceEqual(new[] { _songs[0].Id })));
        await _library.Received(1).GetSongsByPlaylistPagedAsync(newPlaylist, Arg.Any<int>(), Arg.Any<int>(),
            Arg.Any<SongSortOrder>(), Arg.Any<CancellationToken>());
    }
}
