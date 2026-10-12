using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;
using Nagi.WinUI.Services.Abstractions;
using Nagi.WinUI.Models;
using Nagi.WinUI.ViewModels;
using NSubstitute;
using Xunit;

namespace Nagi.WinUI.Tests;

public class FolderSongListViewModelTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task InitializeAsync_ObsoleteFolderLookupCannotUpdateThePage(bool dispose, bool fails)
    {
        var library = new LibraryFakes();
        var oldFolder = new Folder { Name = "Old", Path = @"C:\Old" };
        var pending = new TaskCompletionSource<Folder?>(TaskCreationOptions.RunContinuationsAsynchronously);
        library.Reader.GetFolderByIdAsync(oldFolder.Id).Returns(pending.Task);
        using var viewModel = CreateViewModel(library, Substitute.For<IUISettingsService>());
        var loading = viewModel.InitializeAsync(oldFolder.Name, oldFolder.Id);
        if (dispose) viewModel.Dispose();
        else
        {
            var next = ConfigureRoot(library);
            await viewModel.InitializeAsync(next.Name, next.Id);
        }
        var title = viewModel.PageTitle;
        var breadcrumbs = viewModel.Breadcrumbs.ToArray();

        if (fails) pending.SetException(new IOException("Obsolete lookup failed"));
        else pending.SetResult(oldFolder);
        await loading.WaitAsync(TimeSpan.FromSeconds(5));

        viewModel.PageTitle.Should().Be(title);
        viewModel.Breadcrumbs.Should().Equal(breadcrumbs);
        await library.Reader.DidNotReceive().GetSongsInDirectoryOffsetAsync(oldFolder.Id, Arg.Any<string>(),
            Arg.Any<int>(), Arg.Any<int>(), Arg.Any<SongSortOrder>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PlaySubfolder_LookupCompletesAfterDisposal_DoesNotStartPlayback()
    {
        var library = new LibraryFakes();
        var root = ConfigureRoot(library);
        var folder = new Folder { Name = "Album", Path = @"C:\Music\Album" };
        var pending = new TaskCompletionSource<List<Guid>>(TaskCreationOptions.RunContinuationsAsynchronously);
        library.Reader.GetAllSongIdsInDirectoryRecursiveAsync(root.Id, folder.Path, Arg.Any<SongSortOrder>(), Arg.Any<CancellationToken>())
            .Returns(pending.Task);
        var playback = Substitute.For<IMusicPlaybackService>();
        using var viewModel = CreateViewModel(library, Substitute.For<IUISettingsService>(), playback);
        await viewModel.InitializeAsync(root.Name, root.Id);
        var playing = viewModel.PlaySubfolderCommand.ExecuteAsync(folder);

        viewModel.Dispose();
        pending.SetResult([Guid.NewGuid()]);
        await playing.WaitAsync(TimeSpan.FromSeconds(5));

        await playback.DidNotReceive().PlayAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<int>(),
            Arg.Any<bool?>(), Arg.Any<PlaybackContext?>());
    }

    [Fact]
    public async Task SearchPlayback_UsesTheNavigatedDirectoryAndBreadcrumbReturnsToRoot()
    {
        var library = new LibraryFakes();
        var root = ConfigureRoot(library);
        var child = new Folder { Name = "Album", Path = @"C:\Music\Album" };
        library.Reader.GetFolderByPathAsync(child.Path).Returns(child);
        var song = new Song { Title = "Match" };
        library.Reader.SearchSongsInFolderOffsetAsync(root.Id, child.Path, "Match", Arg.Any<int>(), Arg.Any<int>(),
            Arg.Any<SongSortOrder>(), Arg.Any<CancellationToken>())
            .Returns(new PagedResult<Song> { Items = [song], TotalCount = 1 });
        library.Reader.SearchAllSongIdsInFolderAsync(root.Id, child.Path, "Match", Arg.Any<SongSortOrder>(), Arg.Any<CancellationToken>())
            .Returns(new List<Guid> { song.Id });
        var playback = Substitute.For<IMusicPlaybackService>();
        using var viewModel = CreateViewModel(library, Substitute.For<IUISettingsService>(), playback);
        await viewModel.InitializeAsync(root.Name, root.Id);
        await viewModel.NavigateToSubfolderCommand.ExecuteAsync(child);
        viewModel.IsAtRootLevel.Should().BeFalse();
        viewModel.SearchTerm = "Match";
        await viewModel.SearchAsync();

        await viewModel.PlayAllSongsCommand.ExecuteAsync(null);

        await playback.Received(1).PlayAsync(Arg.Is<IEnumerable<Guid>>(ids => ids.SequenceEqual(new[] { song.Id })),
            0, null, Arg.Any<PlaybackContext?>());
        viewModel.SearchTerm = string.Empty;
        await viewModel.SearchAsync();
        await viewModel.NavigateToBreadcrumbCommand.ExecuteAsync(viewModel.Breadcrumbs[0]);
        viewModel.IsAtRootLevel.Should().BeTrue();
        viewModel.Breadcrumbs.Should().ContainSingle().Which.Path.Should().Be(root.Path);
    }

    [Theory]
    [InlineData(@"C:\", "C:\\", @"C:\Album")]
    [InlineData(@"C:\Music\", "Music", @"C:\Music\Album")]
    [InlineData(@"C:\Music", "Music", @"C:\Music\")]
    public async Task InitializeAsync_BreadcrumbsHandleRootAndTrailingSeparators(
        string rootPath, string rootName, string directoryPath)
    {
        var library = new LibraryFakes();
        var root = new Folder { Name = "Library", Path = rootPath };
        library.Reader.GetFolderByIdAsync(root.Id).Returns(root);
        using var viewModel = CreateViewModel(library, Substitute.For<IUISettingsService>());

        await viewModel.InitializeAsync(root.Name, root.Id, directoryPath);

        viewModel.Breadcrumbs[0].Name.Should().Be(rootName);
        viewModel.Breadcrumbs[0].Path.Should().Be(rootPath);
        viewModel.Breadcrumbs.Count(item => item.IsLast).Should().Be(1);
        viewModel.Breadcrumbs[^1].IsLast.Should().BeTrue();
        viewModel.Breadcrumbs.Select(item => item.Name).Should().Equal(
            directoryPath.EndsWith("Album") ? new[] { rootName, "Album" } : new[] { rootName });
        viewModel.Breadcrumbs[^1].Path.TrimEnd('\\').Should().Be(directoryPath.TrimEnd('\\'));
        viewModel.IsAtRootLevel.Should().Be(!directoryPath.EndsWith("Album"));
    }

    [Fact]
    public async Task RefreshAsync_WhenSettingsFail_ReleasesLoadingStateAndAllowsRetry()
    {
        var settings = Substitute.For<IUISettingsService>();
        settings.GetSongsPerPageAsync().Returns(Task.FromException<int>(new IOException("Settings unavailable")),
            Task.FromResult(50));
        using var viewModel = CreateViewModel(new LibraryFakes(), settings);

        await viewModel.RefreshOrSortSongsAsync();

        viewModel.HasLoadError.Should().BeTrue();
        viewModel.IsLoading.Should().BeFalse();

        await viewModel.RefreshOrSortSongsAsync();

        viewModel.HasLoadError.Should().BeFalse();
        viewModel.IsLoading.Should().BeFalse();
        await settings.Received(2).GetSongsPerPageAsync();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(51)]
    public async Task LoadPageAsync_WhenSongsWereRemoved_UpdatesPageAndContents(int remainingSongs)
    {
        var library = new LibraryFakes();
        var settings = Substitute.For<IUISettingsService>();
        settings.GetSongsPerPageAsync().Returns(50);
        var folder = new Folder { Id = Guid.NewGuid(), Path = @"C:\Music", Name = "Music" };
        library.Reader.GetFolderByIdAsync(folder.Id).Returns(folder);
        var count = 151;
        var song = new Song { Title = "Last song" };
        library.Reader.GetSongsInDirectoryOffsetAsync(folder.Id, folder.Path, Arg.Any<int>(), Arg.Any<int>(),
                Arg.Any<SongSortOrder>(), Arg.Any<CancellationToken>())
            .Returns(call => new PagedResult<Song>
            {
                TotalCount = count,
                Items = call.ArgAt<int>(2) < count ? [song] : []
            });
        using var viewModel = CreateViewModel(library, settings);
        await viewModel.InitializeAsync(folder.Name, folder.Id);
        count = remainingSongs;

        await viewModel.LoadPageAsync(3);

        viewModel.CurrentPage.Should().Be(remainingSongs == 0 ? 1 : 2);
        viewModel.TotalPages.Should().Be(remainingSongs == 0 ? 1 : 2);
        viewModel.HasNextPage.Should().BeFalse();
        viewModel.HasPreviousPage.Should().Be(remainingSongs > 0);
        viewModel.Songs.Should().HaveCount(remainingSongs == 0 ? 0 : 1);
        viewModel.FolderContents.Should().HaveCount(remainingSongs == 0 ? 0 : 1);
    }

    private static FolderSongListViewModel CreateViewModel(LibraryFakes library, IUISettingsService settings,
        IMusicPlaybackService? playback = null) =>
        new(library.Reader, library.Writer, library.Scanner, Substitute.For<IPlaylistService>(), playback ?? Substitute.For<IMusicPlaybackService>(),
            Substitute.For<IMusicNavigationService>(),
            new ImmediateDispatcher(), settings, Substitute.For<IUIService>(),
            NullLogger<FolderSongListViewModel>.Instance);

    [Fact]
    public async Task ResolveSelection_WhenFolderSelectionChangesDuringQuery_ExpandsOriginalFolder()
    {
        var library = new LibraryFakes();
        var root = ConfigureRoot(library);
        var selectedFolder = new Folder { Name = "First", Path = @"C:\Music\First" };
        var nextFolder = new Folder { Name = "Second", Path = @"C:\Music\Second" };
        var songId = Guid.NewGuid();
        var pending = new TaskCompletionSource<List<Guid>>(TaskCreationOptions.RunContinuationsAsynchronously);
        library.Reader.GetAllSongIdsInDirectoryRecursiveAsync(root.Id, root.Path, Arg.Any<SongSortOrder>(), Arg.Any<CancellationToken>())
            .Returns(pending.Task);
        library.Reader.GetAllSongIdsInDirectoryRecursiveAsync(root.Id, selectedFolder.Path, Arg.Any<SongSortOrder>(), Arg.Any<CancellationToken>())
            .Returns(new List<Guid> { songId });
        using var viewModel = new SelectionFolderList(library);
        await viewModel.InitializeAsync(root.Name, root.Id);
        viewModel.FolderContents.Add(FolderContentItem.FromFolder(selectedFolder));
        viewModel.SelectionState.Select(selectedFolder.Id);

        var resolving = viewModel.ResolveSelectionAsync();
        viewModel.FolderContents.Clear();
        viewModel.FolderContents.Add(FolderContentItem.FromFolder(nextFolder));
        viewModel.SelectionState.DeselectAll();
        viewModel.SelectionState.Select(nextFolder.Id);
        pending.SetResult([]);

        (await resolving.WaitAsync(TimeSpan.FromSeconds(10))).Should().Equal(songId);
        await library.Reader.DidNotReceive().GetAllSongIdsInDirectoryRecursiveAsync(root.Id, nextFolder.Path,
            Arg.Any<SongSortOrder>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResolveSelection_WhenViewResetsDuringFolderExpansion_DiscardsResult()
    {
        var library = new LibraryFakes();
        var root = ConfigureRoot(library);
        var selectedFolder = new Folder { Name = "First", Path = @"C:\Music\First" };
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new TaskCompletionSource<List<Guid>>(TaskCreationOptions.RunContinuationsAsynchronously);
        library.Reader.GetAllSongIdsInDirectoryRecursiveAsync(root.Id, root.Path, Arg.Any<SongSortOrder>(), Arg.Any<CancellationToken>())
            .Returns(new List<Guid>());
        library.Reader.GetAllSongIdsInDirectoryRecursiveAsync(root.Id, selectedFolder.Path, Arg.Any<SongSortOrder>(), Arg.Any<CancellationToken>())
            .Returns(_ => { started.SetResult(); return pending.Task; });
        using var viewModel = new SelectionFolderList(library);
        await viewModel.InitializeAsync(root.Name, root.Id);
        viewModel.FolderContents.Add(FolderContentItem.FromFolder(selectedFolder));
        viewModel.SelectionState.Select(selectedFolder.Id);
        var resolving = viewModel.ResolveSelectionAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        viewModel.ResetState();
        pending.SetResult(new List<Guid> { Guid.NewGuid() });

        (await resolving.WaitAsync(TimeSpan.FromSeconds(10))).Should().BeEmpty();
    }

    private static Folder ConfigureRoot(LibraryFakes library)
    {
        var root = new Folder { Name = "Music", Path = @"C:\Music" };
        library.Reader.GetFolderByIdAsync(root.Id).Returns(root);
        library.Reader.GetSongsInDirectoryOffsetAsync(root.Id, root.Path, Arg.Any<int>(), Arg.Any<int>(),
            Arg.Any<SongSortOrder>(), Arg.Any<CancellationToken>()).Returns(new PagedResult<Song>());
        return root;
    }

    private sealed class SelectionFolderList(LibraryFakes library)
        : FolderSongListViewModel(library.Reader, library.Writer, library.Scanner, Substitute.For<IPlaylistService>(), Substitute.For<IMusicPlaybackService>(),
            Substitute.For<IMusicNavigationService>(), new ImmediateDispatcher(),
            Substitute.For<IUISettingsService>(), Substitute.For<IUIService>(), NullLogger<FolderSongListViewModel>.Instance)
    {
        public Task<List<Guid>> ResolveSelectionAsync() => GetCurrentSelectionIdsAsync();
    }
}
