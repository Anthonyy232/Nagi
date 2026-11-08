using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nagi.Core.Playback;
using Nagi.Core.Playlists;
using Nagi.Presentation.Common;
using Nagi.Presentation.Lists;
using Nagi.Presentation.Navigation;
using Nagi.Presentation.Playlists;
using Nagi.Presentation.Resources;
using Nagi.Presentation.Shell;
using Nagi.Presentation.Tests.Utils;
using NSubstitute;
using Xunit;

namespace Nagi.Presentation.Tests.Lists;

public class PlaylistViewModelTests
{
    private static readonly DateTime CoverDate = new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private readonly LibraryFakes _library = new LibraryFakes();
    private readonly ISmartPlaylistService _smart = Substitute.For<ISmartPlaylistService>();
    private readonly IUIService _ui = Substitute.For<IUIService>();

    public PlaylistViewModelTests()
    {
        _library.Playlists.GetAllPlaylistsAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<Playlist>());
        _smart.GetAllSmartPlaylistsAsync().Returns(Array.Empty<SmartPlaylist>());
        _smart.GetAllMatchingSongCountsAsync().Returns(new Dictionary<Guid, int>());
    }

    [Fact]
    public async Task CreatePlaylist_AppliesCurrentSort()
    {
        _library.Playlists.GetAllPlaylistsAsync(Arg.Any<CancellationToken>()).Returns(new[] { new Playlist { Name = "Zebra" } });
        _library.Playlists.CreatePlaylistAsync("Alpha", null, null).Returns(new Playlist { Name = "Alpha" });
        _ui.ShowCreatePlaylistDialogAsync().Returns(new NewPlaylistRequest("Alpha", null));
        using var viewModel = CreateViewModel();
        await viewModel.LoadAsync(CancellationToken.None);
        await viewModel.Sort.ChangeCommand.ExecuteAsync(PlaylistSortOrder.NameAsc);

        await viewModel.CreatePlaylistCommand.ExecuteAsync(null);

        viewModel.Items.Select(playlist => playlist.Name).Should().Equal("Alpha", "Zebra");
    }

    [Fact]
    public async Task CreatePlaylist_RespectsActiveSearchAndRemainsAvailableWhenCleared()
    {
        _library.Playlists.CreatePlaylistAsync("Alpha", null, null).Returns(new Playlist { Name = "Alpha" });
        _ui.ShowCreatePlaylistDialogAsync().Returns(new NewPlaylistRequest("Alpha", null));
        using var viewModel = CreateViewModel();
        viewModel.SearchTerm = "Zebra";
        await viewModel.SearchAsync();

        await viewModel.CreatePlaylistCommand.ExecuteAsync(null);

        viewModel.Items.Should().BeEmpty();
        viewModel.SearchTerm = string.Empty;
        await viewModel.SearchAsync();
        viewModel.Items.Should().ContainSingle().Which.Name.Should().Be("Alpha");
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
            _library.Playlists.GetAllPlaylistsAsync(Arg.Any<CancellationToken>()).Returns(new[] { new Playlist { Id = id, Name = "Morning" } });
            _library.Playlists.RenamePlaylistAsync(id, "Evening").Returns(true);
        }
        using var viewModel = CreateViewModel();
        await viewModel.LoadAsync(CancellationToken.None);
        viewModel.SearchTerm = "Morning";
        await viewModel.SearchAsync();

        _ui.ShowRenamePlaylistDialogAsync("Morning").Returns("Evening");

        await viewModel.RenamePlaylistCommand.ExecuteAsync(viewModel.Items.Single());

        viewModel.Items.Should().BeEmpty();
        viewModel.SearchTerm = "Evening";
        await viewModel.SearchAsync();
        viewModel.Items.Should().ContainSingle().Which.Id.Should().Be(id);
    }

    [Fact]
    public async Task DeletePlaylist_WhenSearchChangesWhileSaving_DoesNotReturnDeletedPlaylist()
    {
        var playlist = new Playlist { Name = "Morning" };
        _library.Playlists.GetAllPlaylistsAsync(Arg.Any<CancellationToken>()).Returns(new[] { playlist });
        var delete = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _library.Playlists.DeletePlaylistAsync(playlist.Id).Returns(delete.Task);
        using var viewModel = CreateViewModel();
        await viewModel.LoadAsync(CancellationToken.None);

        _ui.ShowConfirmationDialogAsync(default!, default!, default, default, default).ReturnsForAnyArgs(true);

        var deleting = viewModel.DeletePlaylistCommand.ExecuteAsync(viewModel.Items.Single());
        viewModel.SearchTerm = "Evening";
        await viewModel.SearchAsync();
        delete.SetResult(true);
        await deleting.WaitAsync(TimeSpan.FromSeconds(10));
        viewModel.SearchTerm = string.Empty;
        await viewModel.SearchAsync();

        viewModel.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task RenamePlaylist_WhenCancelled_LeavesThePlaylistAlone()
    {
        _library.Playlists.GetAllPlaylistsAsync(Arg.Any<CancellationToken>()).Returns(new[] { new Playlist { Name = "Morning" } });
        using var viewModel = CreateViewModel();
        await viewModel.LoadAsync(CancellationToken.None);
        _ui.ShowRenamePlaylistDialogAsync("Morning").Returns((string?)null);

        await viewModel.RenamePlaylistCommand.ExecuteAsync(viewModel.Items.Single());

        await _library.Playlists.DidNotReceiveWithAnyArgs().RenamePlaylistAsync(default, default!);
        viewModel.Items.Single().Name.Should().Be("Morning");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DeletePlaylist_DeletesOnlyAfterConfirmationThatDefaultsToCancel(bool confirmed)
    {
        var playlist = new Playlist { Name = "Morning" };
        _library.Playlists.GetAllPlaylistsAsync(Arg.Any<CancellationToken>()).Returns(new[] { playlist });
        _library.Playlists.DeletePlaylistAsync(playlist.Id).Returns(true);
        _ui.ShowConfirmationDialogAsync(default!, default!, default, default, default).ReturnsForAnyArgs(confirmed);
        using var viewModel = CreateViewModel();
        await viewModel.LoadAsync(CancellationToken.None);

        await viewModel.DeletePlaylistCommand.ExecuteAsync(viewModel.Items.Single());

        await _ui.Received(1).ShowConfirmationDialogAsync(Arg.Any<string>(), Arg.Is<string>(s => s.Contains("Morning")),
            Arg.Any<string?>(), Arg.Any<string?>(), true);
        await _library.Playlists.Received(confirmed ? 1 : 0).DeletePlaylistAsync(playlist.Id);
        viewModel.Items.Should().HaveCount(confirmed ? 0 : 1);
    }

    [Fact]
    public async Task ChangeCover_WhenNoFileIsPicked_KeepsTheCurrentCover()
    {
        _library.Playlists.GetAllPlaylistsAsync(Arg.Any<CancellationToken>()).Returns(new[] { new Playlist { Name = "Artwork" } });
        using var viewModel = CreateViewModel();
        await viewModel.LoadAsync(CancellationToken.None);
        _ui.PickSingleFileAsync(Arg.Any<IEnumerable<string>>()).Returns((string?)null);

        await viewModel.ChangePlaylistCoverCommand.ExecuteAsync(viewModel.Items.Single());

        await _library.Playlists.DidNotReceiveWithAnyArgs().UpdatePlaylistCoverAsync(default, default);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CreateSmartPlaylist_ReloadsOnlyWhenThePlaylistWasSaved(bool saved)
    {
        _ui.ShowSmartPlaylistEditorAsync(null).Returns(saved ? new SmartPlaylist { Name = "Recent" } : null);
        using var viewModel = CreateViewModel();
        _library.Reader.ClearReceivedCalls();

        await viewModel.CreateSmartPlaylistCommand.ExecuteAsync(null);

        await _library.Playlists.Received(saved ? 1 : 0).GetAllPlaylistsAsync(Arg.Any<CancellationToken>());
    }

    private PlaylistViewModel CreateViewModel() => new(_library.Notifier, _library.Playlists, _smart, Substitute.For<IMusicPlaybackService>(),
        Substitute.For<IMusicNavigationService>(), _ui, TestSettings.Create(), new ImmediateDispatcher(),
        NullLogger<PlaylistViewModel>.Instance);

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task LoadPlaylists_IgnoresObsoleteResult(bool fails, bool dispose)
    {
        var pending = new TaskCompletionSource<IEnumerable<Playlist>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _library.Playlists.GetAllPlaylistsAsync(Arg.Any<CancellationToken>()).Returns(pending.Task);
        using var viewModel = CreateViewModel();
        var oldLoad = viewModel.LoadAsync(CancellationToken.None);
        if (dispose) viewModel.Dispose();
        else
        {
            _library.Playlists.GetAllPlaylistsAsync(Arg.Any<CancellationToken>()).Returns(new[] { new Playlist { Name = "Latest" } });
            _library.Playlists.PlaylistsChanged += Raise.Event();
            viewModel.Items.Should().ContainSingle().Which.Name.Should().Be("Latest");
        }

        if (fails) pending.SetException(new IOException("Old load failed"));
        else pending.SetResult(new[] { new Playlist { Name = "Obsolete" } });
        await oldLoad.WaitAsync(TimeSpan.FromSeconds(5));

        viewModel.Items.Should().NotContain(item => item.Name == "Obsolete");
        await _ui.DidNotReceive().ShowMessageDialogAsync(Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task LoadPlaylists_BlocksMutationUntilSnapshotIsApplied()
    {
        var pending = new TaskCompletionSource<IEnumerable<Playlist>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _library.Playlists.GetAllPlaylistsAsync(Arg.Any<CancellationToken>()).Returns(pending.Task);
        using var viewModel = CreateViewModel();
        var loading = viewModel.LoadAsync(CancellationToken.None);

        viewModel.IsAnyOperationInProgress.Should().BeTrue();
        await viewModel.CreatePlaylistCommand.ExecuteAsync(null);
        await _ui.DidNotReceive().ShowCreatePlaylistDialogAsync();
        await _library.Playlists.DidNotReceive().CreatePlaylistAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>());
        pending.SetResult(new[] { new Playlist { Name = "Existing" } });
        await loading.WaitAsync(TimeSpan.FromSeconds(5));

        viewModel.IsAnyOperationInProgress.Should().BeFalse();
        viewModel.Items.Should().ContainSingle().Which.Name.Should().Be("Existing");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PlayRandomPlaylist_WhenSelectedTypeWasDeleted_ClearsPickingStatus(bool smart)
    {
        _library.Playlists.GetPlaylistCountAsync().Returns(smart ? 0 : 1);
        _smart.GetSmartPlaylistCountAsync().Returns(smart ? 1 : 0);
        using var viewModel = CreateViewModel();

        await viewModel.PlayRandomPlaylistCommand.ExecuteAsync(null);

        await _ui.Received(1).ShowMessageDialogAsync(Strings.PlaylistPage_Title, Strings.Playlist_Status_NoPlaylists);
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
                _smart.PlaylistUpdated += Raise.EventWith(new PlaylistUpdatedEventArgs(id, null, DateTime.UtcNow));
                return true;
            });
        }
        else
        {
            _library.Playlists.GetAllPlaylistsAsync(Arg.Any<CancellationToken>()).Returns(new[] { new Playlist { Id = id, CoverImageUri = "ms-appx:///custom.png" } });
            _library.Playlists.UpdatePlaylistCoverAsync(id, null).Returns(_ =>
            {
                _library.Playlists.PlaylistUpdated += Raise.EventWith(new PlaylistUpdatedEventArgs(id, null, DateTime.UtcNow));
                return true;
            });
        }
        using var viewModel = CreateViewModel();
        await viewModel.LoadAsync(CancellationToken.None);

        await viewModel.RemovePlaylistCoverCommand.ExecuteAsync(viewModel.Items.Single());

        viewModel.Items.Should().ContainSingle().Which.IsArtworkAvailable.Should().BeFalse();
        viewModel.IsAnyOperationInProgress.Should().BeFalse();
        await _ui.DidNotReceive().ShowMessageDialogAsync(Arg.Any<string>(), Arg.Any<string>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangeCover_UsesPersistedImagePathFromServiceEvent(bool smart)
    {
        var id = Guid.NewGuid();
        const string selectedImage = "ms-appx:///selected.png";
        const string cachedImage = "ms-appx:///cache/playlist.custom.png";
        if (smart)
        {
            _smart.GetAllSmartPlaylistsAsync().Returns(new[] { new SmartPlaylist { Id = id, Name = "Artwork" } });
            _smart.UpdateSmartPlaylistCoverAsync(id, selectedImage).Returns(_ =>
            {
                _smart.PlaylistUpdated += Raise.EventWith(new PlaylistUpdatedEventArgs(id, cachedImage, CoverDate));
                return Task.FromResult(true);
            });
        }
        else
        {
            _library.Playlists.GetAllPlaylistsAsync(Arg.Any<CancellationToken>()).Returns(new[] { new Playlist { Id = id, Name = "Artwork" } });
            _library.Playlists.UpdatePlaylistCoverAsync(id, selectedImage).Returns(_ =>
            {
                _library.Playlists.PlaylistUpdated += Raise.EventWith(new PlaylistUpdatedEventArgs(id, cachedImage, CoverDate));
                return Task.FromResult(true);
            });
        }
        using var viewModel = CreateViewModel();
        await viewModel.LoadAsync(CancellationToken.None);

        _ui.PickSingleFileAsync(Arg.Any<IEnumerable<string>>()).Returns(selectedImage);

        await viewModel.ChangePlaylistCoverCommand.ExecuteAsync(viewModel.Items.Single());

        viewModel.Items.Should().ContainSingle().Which.CoverImageUri.Should().Be(ImageUriHelper.GetUriWithCacheBuster(cachedImage, CoverDate));
        viewModel.Items[0].IsCustomImage.Should().BeTrue();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PlaylistUpdated_RefreshesModificationSortAndHiddenEntries(bool smart)
    {
        var oldDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var updatedDate = oldDate.AddDays(2);
        var id = Guid.NewGuid();
        _library.Playlists.GetAllPlaylistsAsync(Arg.Any<CancellationToken>()).Returns(new[]
        {
            new Playlist { Name = "Recent", DateModified = oldDate.AddDays(1) }
        });
        if (smart)
            _smart.GetAllSmartPlaylistsAsync().Returns(new[] { new SmartPlaylist { Id = id, Name = "Older", DateModified = oldDate } });
        else
            _library.Playlists.GetAllPlaylistsAsync(Arg.Any<CancellationToken>()).Returns(new[]
            {
                new Playlist { Name = "Recent", DateModified = oldDate.AddDays(1) },
                new Playlist { Id = id, Name = "Older", DateModified = oldDate }
            });
        using var viewModel = CreateViewModel();
        await viewModel.LoadAsync(CancellationToken.None);
        await viewModel.Sort.ChangeCommand.ExecuteAsync(PlaylistSortOrder.DateModifiedDesc);
        viewModel.SearchTerm = "Recent";
        await viewModel.SearchAsync();

        var update = new PlaylistUpdatedEventArgs(id, null, updatedDate);
        if (smart) _smart.PlaylistUpdated += Raise.EventWith(update);
        else _library.Playlists.PlaylistUpdated += Raise.EventWith(update);
        viewModel.SearchTerm = string.Empty;
        await viewModel.SearchAsync();

        viewModel.Items[0].Id.Should().Be(id);
        viewModel.Items[0].DateModified.Should().Be(updatedDate);
    }

    [Fact]
    public async Task PlaylistUpdated_WithReplacedImage_BypassesCachedFileVersion()
    {
        var image = Path.GetTempFileName();
        try
        {
            var playlist = new Playlist { Name = "Artwork", CoverImageUri = image };
            _library.Playlists.GetAllPlaylistsAsync(Arg.Any<CancellationToken>()).Returns(new[] { playlist });
            using var viewModel = CreateViewModel();
            await viewModel.LoadAsync(CancellationToken.None);
            var originalUri = viewModel.Items[0].CoverImageUri;
            var modified = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);

            _library.Playlists.PlaylistUpdated += Raise.EventWith(new PlaylistUpdatedEventArgs(playlist.Id, image, modified));

            viewModel.Items[0].CoverImageUri.Should().NotBe(originalUri);
            viewModel.Items[0].CoverImageUri.Should().Contain($"?v={modified.Ticks}");
        }
        finally
        {
            File.Delete(image);
        }
    }
}
