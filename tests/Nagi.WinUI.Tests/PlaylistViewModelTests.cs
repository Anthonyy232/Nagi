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

public class PlaylistViewModelTests
{
    private readonly ILibraryService _library = Substitute.For<ILibraryService>();
    private readonly ISmartPlaylistService _smart = Substitute.For<ISmartPlaylistService>();

    public PlaylistViewModelTests()
    {
        _library.GetAllPlaylistsAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<Playlist>());
        _smart.GetAllSmartPlaylistsAsync().Returns(Array.Empty<SmartPlaylist>());
        _smart.GetAllMatchingSongCountsAsync().Returns(new Dictionary<Guid, int>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EmptyPlaylist_DisplaysZeroSongCount(bool smart)
    {
        var item = smart ? new PlaylistViewModelItem(new SmartPlaylist { Name = "Empty" })
            : new PlaylistViewModelItem(new Playlist { Name = "Empty" });

        item.SongCountText.Should().Contain("0");
    }

    [Fact]
    public async Task CreatePlaylist_AppliesCurrentSort()
    {
        _library.GetAllPlaylistsAsync(Arg.Any<CancellationToken>()).Returns(new[] { new Playlist { Name = "Zebra" } });
        _library.CreatePlaylistAsync("Alpha", null, null).Returns(new Playlist { Name = "Alpha" });
        using var viewModel = CreateViewModel();
        await viewModel.LoadPlaylistsCommand.ExecuteAsync(null);
        await viewModel.ChangeSortOrderAsync(nameof(PlaylistSortOrder.NameAsc));

        await viewModel.CreatePlaylistCommand.ExecuteAsync(Tuple.Create("Alpha", (string?)null));

        viewModel.Playlists.Select(playlist => playlist.Name).Should().Equal("Alpha", "Zebra");
    }

    [Fact]
    public async Task CreatePlaylist_RespectsActiveSearchAndRemainsAvailableWhenCleared()
    {
        _library.CreatePlaylistAsync("Alpha", null, null).Returns(new Playlist { Name = "Alpha" });
        using var viewModel = CreateViewModel();
        viewModel.SearchTerm = "Zebra";
        await viewModel.SearchAsync();

        await viewModel.CreatePlaylistCommand.ExecuteAsync(Tuple.Create("Alpha", (string?)null));

        viewModel.Playlists.Should().BeEmpty();
        viewModel.SearchTerm = string.Empty;
        await viewModel.SearchAsync();
        viewModel.Playlists.Should().ContainSingle().Which.Name.Should().Be("Alpha");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RenamePlaylist_ReappliesSearch(bool smart)
    {
        var id = Guid.NewGuid();
        if (smart)
        {
            _smart.GetAllSmartPlaylistsAsync().Returns(new[] { new SmartPlaylist { Id = id, Name = "Morning" } });
            _smart.RenameSmartPlaylistAsync(id, "Evening").Returns(true);
        }
        else
        {
            _library.GetAllPlaylistsAsync(Arg.Any<CancellationToken>()).Returns(new[] { new Playlist { Id = id, Name = "Morning" } });
            _library.RenamePlaylistAsync(id, "Evening").Returns(true);
        }
        using var viewModel = CreateViewModel();
        await viewModel.LoadPlaylistsCommand.ExecuteAsync(null);
        viewModel.SearchTerm = "Morning";
        await viewModel.SearchAsync();

        await viewModel.RenamePlaylistCommand.ExecuteAsync(Tuple.Create(id, "Evening", smart));

        viewModel.Playlists.Should().BeEmpty();
        viewModel.SearchTerm = "Evening";
        await viewModel.SearchAsync();
        viewModel.Playlists.Should().ContainSingle().Which.Id.Should().Be(id);
    }

    [Fact]
    public async Task DeletePlaylist_WhenSearchChangesWhileSaving_DoesNotReturnDeletedPlaylist()
    {
        var playlist = new Playlist { Name = "Morning" };
        _library.GetAllPlaylistsAsync(Arg.Any<CancellationToken>()).Returns(new[] { playlist });
        var delete = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _library.DeletePlaylistAsync(playlist.Id).Returns(delete.Task);
        using var viewModel = CreateViewModel();
        await viewModel.LoadPlaylistsCommand.ExecuteAsync(null);

        var deleting = viewModel.DeletePlaylistCommand.ExecuteAsync(Tuple.Create(playlist.Id, false));
        viewModel.SearchTerm = "Evening";
        await viewModel.SearchAsync();
        delete.SetResult(true);
        await deleting.WaitAsync(TimeSpan.FromSeconds(10));
        viewModel.SearchTerm = string.Empty;
        await viewModel.SearchAsync();

        viewModel.Playlists.Should().BeEmpty();
    }

    private PlaylistViewModel CreateViewModel() => new(_library, _library, _smart, Substitute.For<IMusicPlaybackService>(),
        Substitute.For<INavigationService>(), Substitute.For<IUISettingsService>(), new ImmediateDispatcher(),
        NullLogger<PlaylistViewModel>.Instance);

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task LoadPlaylists_IgnoresObsoleteResult(bool fails, bool dispose)
    {
        var pending = new TaskCompletionSource<IEnumerable<Playlist>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _library.GetAllPlaylistsAsync(Arg.Any<CancellationToken>()).Returns(pending.Task);
        using var viewModel = CreateViewModel();
        var oldLoad = viewModel.LoadPlaylistsCommand.ExecuteAsync(null);
        if (dispose) viewModel.Dispose();
        else
        {
            _library.GetAllPlaylistsAsync(Arg.Any<CancellationToken>()).Returns(new[] { new Playlist { Name = "Latest" } });
            _library.PlaylistsChanged += Raise.Event();
            viewModel.Playlists.Should().ContainSingle().Which.Name.Should().Be("Latest");
        }
        var status = viewModel.StatusMessage;

        if (fails) pending.SetException(new IOException("Old load failed"));
        else pending.SetResult(new[] { new Playlist { Name = "Obsolete" } });
        await oldLoad.WaitAsync(TimeSpan.FromSeconds(5));

        viewModel.Playlists.Should().NotContain(item => item.Name == "Obsolete");
        viewModel.StatusMessage.Should().Be(status);
    }

    [Fact]
    public async Task LoadPlaylists_BlocksMutationUntilSnapshotIsApplied()
    {
        var pending = new TaskCompletionSource<IEnumerable<Playlist>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _library.GetAllPlaylistsAsync(Arg.Any<CancellationToken>()).Returns(pending.Task);
        using var viewModel = CreateViewModel();
        var loading = viewModel.LoadPlaylistsCommand.ExecuteAsync(null);

        viewModel.IsAnyOperationInProgress.Should().BeTrue();
        await viewModel.CreatePlaylistCommand.ExecuteAsync(Tuple.Create("New", (string?)null));
        await _library.DidNotReceive().CreatePlaylistAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>());
        pending.SetResult(new[] { new Playlist { Name = "Existing" } });
        await loading.WaitAsync(TimeSpan.FromSeconds(5));

        viewModel.IsAnyOperationInProgress.Should().BeFalse();
        viewModel.Playlists.Should().ContainSingle().Which.Name.Should().Be("Existing");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PlayRandomPlaylist_WhenSelectedTypeWasDeleted_ClearsPickingStatus(bool smart)
    {
        _library.GetPlaylistCountAsync().Returns(smart ? 0 : 1);
        _smart.GetSmartPlaylistCountAsync().Returns(smart ? 1 : 0);
        using var viewModel = CreateViewModel();

        await viewModel.PlayRandomPlaylistCommand.ExecuteAsync(null);

        viewModel.StatusMessage.Should().Be(Nagi.WinUI.Resources.Strings.Playlist_Status_NoPlaylists);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemoveCover_ClearsCachedImageFromServiceEvent(bool smart)
    {
        var id = Guid.NewGuid();
        if (smart)
        {
            _smart.GetAllSmartPlaylistsAsync().Returns(new[] { new SmartPlaylist { Id = id, CoverImageUri = "ms-appx:///custom.png" } });
            _smart.UpdateSmartPlaylistCoverAsync(id, null).Returns(_ =>
            {
                _smart.PlaylistUpdated += Raise.EventWith(new PlaylistUpdatedEventArgs(id, null));
                return true;
            });
        }
        else
        {
            _library.GetAllPlaylistsAsync(Arg.Any<CancellationToken>()).Returns(new[] { new Playlist { Id = id, CoverImageUri = "ms-appx:///custom.png" } });
            _library.UpdatePlaylistCoverAsync(id, null).Returns(_ =>
            {
                _library.PlaylistUpdated += Raise.EventWith(new PlaylistUpdatedEventArgs(id, null));
                return true;
            });
        }
        using var viewModel = CreateViewModel();
        await viewModel.LoadPlaylistsCommand.ExecuteAsync(null);

        await viewModel.RemovePlaylistCoverCommand.ExecuteAsync(Tuple.Create(id, smart));

        viewModel.Playlists.Should().ContainSingle().Which.IsArtworkAvailable.Should().BeFalse();
        viewModel.IsAnyOperationInProgress.Should().BeFalse();
        viewModel.StatusMessage.Should().BeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UpdateCover_UsesPersistedImagePathFromServiceEvent(bool smart)
    {
        var id = Guid.NewGuid();
        const string selectedImage = "ms-appx:///selected.png";
        const string cachedImage = "ms-appx:///cache/playlist.custom.png";
        if (smart)
        {
            _smart.GetAllSmartPlaylistsAsync().Returns(new[] { new SmartPlaylist { Id = id, Name = "Artwork" } });
            _smart.UpdateSmartPlaylistCoverAsync(id, selectedImage).Returns(_ =>
            {
                _smart.PlaylistUpdated += Raise.EventWith(new PlaylistUpdatedEventArgs(id, cachedImage));
                return Task.FromResult(true);
            });
        }
        else
        {
            _library.GetAllPlaylistsAsync(Arg.Any<CancellationToken>()).Returns(new[] { new Playlist { Id = id, Name = "Artwork" } });
            _library.UpdatePlaylistCoverAsync(id, selectedImage).Returns(_ =>
            {
                _library.PlaylistUpdated += Raise.EventWith(new PlaylistUpdatedEventArgs(id, cachedImage));
                return Task.FromResult(true);
            });
        }
        using var viewModel = CreateViewModel();
        await viewModel.LoadPlaylistsCommand.ExecuteAsync(null);

        await viewModel.UpdatePlaylistCoverCommand.ExecuteAsync(Tuple.Create(id, selectedImage, smart));

        viewModel.Playlists.Should().ContainSingle().Which.CoverImageUri.Should().Be(cachedImage);
        viewModel.Playlists[0].IsCustomImage.Should().BeTrue();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PlaylistUpdated_RefreshesModificationSortAndHiddenEntries(bool smart)
    {
        var oldDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var updatedDate = oldDate.AddDays(2);
        var id = Guid.NewGuid();
        _library.GetAllPlaylistsAsync(Arg.Any<CancellationToken>()).Returns(new[]
        {
            new Playlist { Name = "Recent", DateModified = oldDate.AddDays(1) }
        });
        if (smart)
            _smart.GetAllSmartPlaylistsAsync().Returns(new[] { new SmartPlaylist { Id = id, Name = "Older", DateModified = oldDate } });
        else
            _library.GetAllPlaylistsAsync(Arg.Any<CancellationToken>()).Returns(new[]
            {
                new Playlist { Name = "Recent", DateModified = oldDate.AddDays(1) },
                new Playlist { Id = id, Name = "Older", DateModified = oldDate }
            });
        using var viewModel = CreateViewModel();
        await viewModel.LoadPlaylistsCommand.ExecuteAsync(null);
        await viewModel.ChangeSortOrderAsync(nameof(PlaylistSortOrder.DateModifiedDesc));
        viewModel.SearchTerm = "Recent";
        await viewModel.SearchAsync();

        var update = new PlaylistUpdatedEventArgs(id, null, updatedDate);
        if (smart) _smart.PlaylistUpdated += Raise.EventWith(update);
        else _library.PlaylistUpdated += Raise.EventWith(update);
        viewModel.SearchTerm = string.Empty;
        await viewModel.SearchAsync();

        viewModel.Playlists[0].Id.Should().Be(id);
        viewModel.Playlists[0].DateModified.Should().Be(updatedDate);
    }

    [Fact]
    public async Task PlaylistUpdated_WithReplacedImage_BypassesCachedFileVersion()
    {
        var image = Path.GetTempFileName();
        try
        {
            var playlist = new Playlist { Name = "Artwork", CoverImageUri = image };
            _library.GetAllPlaylistsAsync(Arg.Any<CancellationToken>()).Returns(new[] { playlist });
            using var viewModel = CreateViewModel();
            await viewModel.LoadPlaylistsCommand.ExecuteAsync(null);
            var originalUri = viewModel.Playlists[0].CoverImageUri;
            var modified = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);

            _library.PlaylistUpdated += Raise.EventWith(new PlaylistUpdatedEventArgs(playlist.Id, image, modified));

            viewModel.Playlists[0].CoverImageUri.Should().NotBe(originalUri);
            viewModel.Playlists[0].CoverImageUri.Should().Contain($"?v={modified.Ticks}");
        }
        finally
        {
            File.Delete(image);
        }
    }
}
