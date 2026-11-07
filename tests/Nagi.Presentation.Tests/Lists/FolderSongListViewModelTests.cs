using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nagi.Core.Library;
using Nagi.Core.Playback;
using Nagi.Core.Playlists;
using Nagi.Presentation.Lists;
using Nagi.Presentation.Navigation;
using Nagi.Presentation.Shell;
using Nagi.Presentation.Tests.Utils;
using NSubstitute;
using Xunit;

namespace Nagi.Presentation.Tests.Lists;

public class FolderSongListViewModelTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Open_ObsoleteFolderLookupCannotUpdateThePage(bool dispose, bool fails)
    {
        var library = new LibraryFakes();
        var oldFolder = new Folder { Name = "Old", Path = @"C:\Old" };
        var pending = new TaskCompletionSource<Folder?>(TaskCreationOptions.RunContinuationsAsynchronously);
        library.Reader.GetFolderByIdAsync(oldFolder.Id).Returns(pending.Task);
        using var viewModel = CreateViewModel(library);
        var loading = viewModel.OpenAsync(new FolderSongViewNavigationParameter { Title = oldFolder.Name, FolderId = oldFolder.Id });
        if (dispose) viewModel.Dispose();
        else
        {
            var next = ConfigureRoot(library);
            await viewModel.OpenAsync(new FolderSongViewNavigationParameter { Title = next.Name, FolderId = next.Id });
        }
        var title = viewModel.PageTitle;
        var breadcrumbs = viewModel.Breadcrumbs.ToArray();

        if (fails) pending.SetException(new IOException("Obsolete lookup failed"));
        else pending.SetResult(oldFolder);
        await loading.WaitAsync(TimeSpan.FromSeconds(5));

        viewModel.PageTitle.Should().Be(title);
        viewModel.Breadcrumbs.Should().Equal(breadcrumbs);
        await library.Reader.DidNotReceive().GetDirectoryPageAsync(
            new SongScope.Directory(oldFolder.Id, oldFolder.Path), Arg.Any<string?>(),
            Arg.Any<SongSortOrder>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PlaySubfolder_LookupCompletesAfterDisposal_DoesNotStartPlayback()
    {
        var library = new LibraryFakes();
        var root = ConfigureRoot(library);
        var folder = new Folder { Name = "Album", Path = @"C:\Music\Album" };
        var pending = new TaskCompletionSource<List<Guid>>(TaskCreationOptions.RunContinuationsAsynchronously);
        library.Reader.GetSongIdsAsync(new SongScope.DirectoryTree(root.Id, folder.Path), null, Arg.Any<SongSortOrder>(), Arg.Any<CancellationToken>())
            .Returns(pending.Task);
        var playback = Substitute.For<IMusicPlaybackService>();
        using var viewModel = CreateViewModel(library, playback);
        await viewModel.OpenAsync(new FolderSongViewNavigationParameter { Title = root.Name, FolderId = root.Id });
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
        var song = new Song { Title = "Match" };
        library.Reader.GetDirectoryPageAsync(new SongScope.Directory(root.Id, child.Path), Arg.Any<string?>(), Arg.Any<SongSortOrder>(),
            Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new DirectoryPage { Items = [song], TotalCount = 1 });
        library.Reader.GetSongIdsAsync(new SongScope.Directory(root.Id, child.Path), "Match", Arg.Any<SongSortOrder>(), Arg.Any<CancellationToken>())
            .Returns(new List<Guid> { song.Id });
        var playback = Substitute.For<IMusicPlaybackService>();
        using var viewModel = CreateViewModel(library, playback);
        await viewModel.OpenAsync(new FolderSongViewNavigationParameter { Title = root.Name, FolderId = root.Id });
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
    public async Task Breadcrumbs_HandleRootAndTrailingSeparators(
        string rootPath, string rootName, string directoryPath)
    {
        var library = new LibraryFakes();
        var root = new Folder { Name = "Library", Path = rootPath };
        library.Reader.GetFolderByIdAsync(root.Id).Returns(root);
        using var viewModel = CreateViewModel(library);

        await viewModel.OpenAsync(new FolderSongViewNavigationParameter { Title = root.Name, FolderId = root.Id });
        await viewModel.NavigateToSubfolderCommand.ExecuteAsync(new Folder { Path = directoryPath });

        viewModel.Breadcrumbs[0].Name.Should().Be(rootName);
        viewModel.Breadcrumbs[0].Path.Should().Be(rootPath);
        viewModel.Breadcrumbs.Count(item => item.IsLast).Should().Be(1);
        viewModel.Breadcrumbs[^1].IsLast.Should().BeTrue();
        viewModel.Breadcrumbs.Select(item => item.Name).Should().Equal(
            directoryPath.EndsWith("Album") ? new[] { rootName, "Album" } : new[] { rootName });
        viewModel.Breadcrumbs[^1].Path.TrimEnd('\\').Should().Be(directoryPath.TrimEnd('\\'));
        viewModel.IsAtRootLevel.Should().Be(!directoryPath.EndsWith("Album"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(51)]
    public async Task Load_WhenSongsWereRemoved_UpdatesPageAndContents(int remainingSongs)
    {
        var library = new LibraryFakes();
        var folder = new Folder { Id = Guid.NewGuid(), Path = @"C:\Music", Name = "Music" };
        library.Reader.GetFolderByIdAsync(folder.Id).Returns(folder);
        var count = 151;
        var song = new Song { Title = "Last song" };
        library.Reader.GetDirectoryPageAsync(new SongScope.Directory(folder.Id, folder.Path), Arg.Any<string?>(), Arg.Any<SongSortOrder>(),
                Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(call => new DirectoryPage
            {
                TotalCount = count,
                PageNumber = call.ArgAt<int>(3),
                PageSize = call.ArgAt<int>(4),
                Items = (call.ArgAt<int>(3) - 1) * call.ArgAt<int>(4) < count ? [song] : []
            });
        using var viewModel = CreateViewModel(library);
        await viewModel.OpenAsync(new FolderSongViewNavigationParameter { Title = folder.Name, FolderId = folder.Id });
        count = remainingSongs;

        viewModel.CurrentPage = 3;
        await viewModel.LoadAsync(CancellationToken.None);

        viewModel.CurrentPage.Should().Be(remainingSongs == 0 ? 1 : 2);
        viewModel.TotalPages.Should().Be(remainingSongs == 0 ? 1 : 2);
        viewModel.HasNextPage.Should().BeFalse();
        viewModel.HasPreviousPage.Should().Be(remainingSongs > 0);
        viewModel.Songs.Should().HaveCount(remainingSongs == 0 ? 0 : 1);
        viewModel.FolderContents.Should().HaveCount(remainingSongs == 0 ? 0 : 1);
    }

    private static FolderSongListViewModel CreateViewModel(LibraryFakes library, IMusicPlaybackService? playback = null) =>
        new(library.Reader, library.Listening, library.Notifier, Substitute.For<IPlaylistService>(), playback ?? Substitute.For<IMusicPlaybackService>(),
            Substitute.For<IMusicNavigationService>(),
            new ImmediateDispatcher(), TestSettings.Create(), Substitute.For<IUIService>(),
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
        library.Reader.GetSongIdsAsync(new SongScope.DirectoryTree(root.Id, selectedFolder.Path), null, Arg.Any<SongSortOrder>(), Arg.Any<CancellationToken>())
            .Returns(pending.Task);
        using var viewModel = new SelectionFolderList(library);
        await viewModel.OpenAsync(new FolderSongViewNavigationParameter { Title = root.Name, FolderId = root.Id });
        viewModel.OnSongsSelectionChanged([FolderContentItem.FromFolder(selectedFolder)]);

        var resolving = viewModel.ResolveSelectionAsync();
        viewModel.OnSongsSelectionChanged([FolderContentItem.FromFolder(nextFolder)]);
        pending.SetResult([songId]);

        (await resolving.WaitAsync(TimeSpan.FromSeconds(10))).Should().Equal(songId);
        await library.Reader.DidNotReceive().GetSongIdsAsync(new SongScope.DirectoryTree(root.Id, nextFolder.Path), null,
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
        library.Reader.GetSongIdsAsync(new SongScope.DirectoryTree(root.Id, root.Path), null, Arg.Any<SongSortOrder>(), Arg.Any<CancellationToken>())
            .Returns(new List<Guid>());
        library.Reader.GetSongIdsAsync(new SongScope.DirectoryTree(root.Id, selectedFolder.Path), null, Arg.Any<SongSortOrder>(), Arg.Any<CancellationToken>())
            .Returns(_ => { started.SetResult(); return pending.Task; });
        using var viewModel = new SelectionFolderList(library);
        await viewModel.OpenAsync(new FolderSongViewNavigationParameter { Title = root.Name, FolderId = root.Id });
        viewModel.OnSongsSelectionChanged([FolderContentItem.FromFolder(selectedFolder)]);
        var resolving = viewModel.ResolveSelectionAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        viewModel.ResetState();
        pending.SetResult(new List<Guid> { Guid.NewGuid() });

        (await resolving.WaitAsync(TimeSpan.FromSeconds(10))).Should().BeEmpty();
    }

    [Fact]
    public async Task FolderWithOnlySubfolders_CanPlayAll()
    {
        var library = new LibraryFakes();
        var root = ConfigureRoot(library);
        library.Reader.GetDirectoryPageAsync(Arg.Any<SongScope.Directory>(), Arg.Any<string?>(), Arg.Any<SongSortOrder>(),
                Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new DirectoryPage { Folders = [new Folder { Name = "Album", Path = @"C:\Music\Album" }], FolderCount = 1, TotalCount = 1 });
        using var viewModel = CreateViewModel(library);

        await viewModel.OpenAsync(new FolderSongViewNavigationParameter { Title = root.Name, FolderId = root.Id });

        viewModel.Songs.Should().BeEmpty();
        viewModel.PlayAllSongsCommand.CanExecute(null).Should().BeTrue();
    }

    private static Folder ConfigureRoot(LibraryFakes library)
    {
        var root = new Folder { Name = "Music", Path = @"C:\Music" };
        library.Reader.GetFolderByIdAsync(root.Id).Returns(root);
        library.Reader.GetDirectoryPageAsync(new SongScope.Directory(root.Id, root.Path), Arg.Any<string?>(), Arg.Any<SongSortOrder>(),
            Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(new DirectoryPage());
        return root;
    }

    private sealed class SelectionFolderList(LibraryFakes library)
        : FolderSongListViewModel(library.Reader, library.Listening, library.Notifier, Substitute.For<IPlaylistService>(), Substitute.For<IMusicPlaybackService>(),
            Substitute.For<IMusicNavigationService>(), new ImmediateDispatcher(),
            TestSettings.Create(), Substitute.For<IUIService>(), NullLogger<FolderSongListViewModel>.Instance)
    {
        public Task<List<Guid>> ResolveSelectionAsync() => GetCurrentSelectionIdsAsync();
    }
}
