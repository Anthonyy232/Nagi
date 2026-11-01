using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;
using Nagi.Presentation.Resources;
using Nagi.Presentation.Services.Abstractions;
using Nagi.Presentation.ViewModels;
using NSubstitute;
using Xunit;

namespace Nagi.Presentation.Tests;

public class FolderViewModelTests
{
    [Fact]
    public async Task CancelledFolderLoad_DoesNotApplyLateResults()
    {
        var library = new LibraryFakes();
        var response = new TaskCompletionSource<IEnumerable<FolderSummary>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        library.Reader.GetRootFoldersWithCountsAsync(Arg.Any<CancellationToken>()).Returns(call =>
        {
            started.SetResult(call.Arg<CancellationToken>());
            return response.Task;
        });
        using var viewModel = CreateViewModel(library, Substitute.For<IUIService>());
        var load = viewModel.LoadFoldersCommand.ExecuteAsync(null);
        try
        {
            var token = await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            viewModel.LoadFoldersCommand.Cancel();
            token.IsCancellationRequested.Should().BeTrue();

            response.SetResult(new[] { new FolderSummary(new Folder { Name = "Stale folder" }, 2, IsServer: false) });
            await load;

            viewModel.Folders.Should().BeEmpty();
        }
        finally
        {
            response.TrySetResult(Array.Empty<FolderSummary>());
            await load;
        }
    }

    [Fact]
    public async Task AddFolder_CancelledPickerAddsNothing()
    {
        var library = new LibraryFakes();
        var ui = Substitute.For<IUIService>();
        ui.PickSingleFolderAsync().Returns((string?)null);
        using var viewModel = CreateViewModel(library, ui);

        await viewModel.AddFolderCommand.ExecuteAsync(null);

        await library.Writer.DidNotReceiveWithAnyArgs().AddFolderAsync(default!, default);
    }

    [Fact]
    public async Task AddFolder_FolderAlreadyInLibrary_TellsTheUserAndAddsNothing()
    {
        var library = new LibraryFakes();
        var folder = new Folder { Name = "Music", Path = @"C:\Music" };
        library.Reader.GetRootFoldersWithCountsAsync(Arg.Any<CancellationToken>()).Returns(new[] { new FolderSummary(folder, 1, IsServer: false) });
        var ui = Substitute.For<IUIService>();
        ui.PickSingleFolderAsync().Returns(@"c:\music\");
        using var viewModel = CreateViewModel(library, ui);
        await viewModel.LoadFoldersCommand.ExecuteAsync(null);

        await viewModel.AddFolderCommand.ExecuteAsync(null);

        await ui.Received(1).ShowMessageDialogAsync(Arg.Any<string>(), Strings.Folders_AddFolder_Exists);
        await library.Writer.DidNotReceiveWithAnyArgs().AddFolderAsync(default!, default);
    }

    [Fact]
    public async Task AddFolder_OverlappingAnExistingFolder_TellsTheUserWhyAndScansNothing()
    {
        var library = new LibraryFakes();
        library.Writer.AddFolderAsync(@"C:\Music\Rock").Returns<Folder?>(_ => throw new FolderOverlapsException(@"C:\Music\Rock"));
        var ui = Substitute.For<IUIService>();
        ui.PickSingleFolderAsync().Returns(@"C:\Music\Rock");
        using var viewModel = CreateViewModel(library, ui);

        await viewModel.AddFolderCommand.ExecuteAsync(null);

        await ui.Received(1).ShowMessageDialogAsync(Strings.FolderPage_AddFolder_Label, Strings.Folders_AddFolder_Overlaps);
        await library.Scanner.DidNotReceiveWithAnyArgs().ScanFolderForMusicAsync(default!, default, default);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DeleteFolder_RemovesOnlyAfterConfirmation(bool confirmed)
    {
        var library = new LibraryFakes();
        var folder = new Folder { Name = "Music", Path = @"C:Music" };
        library.Reader.GetRootFoldersWithCountsAsync(Arg.Any<CancellationToken>()).Returns(new[] { new FolderSummary(folder, 1, IsServer: false) });
        library.Writer.RemoveFolderAsync(folder.Id).Returns(true);
        var ui = Substitute.For<IUIService>();
        ui.ShowConfirmationDialogAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<bool>()).Returns(confirmed);
        using var viewModel = CreateViewModel(library, ui);

        await viewModel.LoadFoldersCommand.ExecuteAsync(null);

        await viewModel.DeleteFolderCommand.ExecuteAsync(folder.Id);

        await library.Writer.Received(confirmed ? 1 : 0).RemoveFolderAsync(folder.Id);
    }

    [Fact]
    public async Task DeleteFolder_Server_IsRemovedThroughItsServerSoNagiSignsOut()
    {
        var library = new LibraryFakes();
        var server = new Folder { Name = "Jellyfin", Path = "nagi-server:0123" };
        library.Reader.GetRootFoldersWithCountsAsync(Arg.Any<CancellationToken>()).Returns(new[] { new FolderSummary(server, 1, IsServer: true) });
        var musicServers = Substitute.For<IMusicServerService>();
        musicServers.RemoveAsync(server.Id).Returns(true);
        var ui = Substitute.For<IUIService>();
        ui.ShowConfirmationDialogAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<bool>()).Returns(true);
        using var viewModel = CreateViewModel(library, ui, musicServers);
        await viewModel.LoadFoldersCommand.ExecuteAsync(null);

        await viewModel.DeleteFolderCommand.ExecuteAsync(server.Id);

        await musicServers.Received(1).RemoveAsync(server.Id);
        await library.Writer.DidNotReceive().RemoveFolderAsync(server.Id);
        await ui.DidNotReceiveWithAnyArgs().ShowMessageDialogAsync(default!, default!);
    }

    [Fact]
    public async Task SyncServer_SavedSignInRejected_OffersToSignInAgainThenSyncs()
    {
        var library = new LibraryFakes();
        var server = new Folder { Name = "Navidrome", Path = "nagi-server:0123" };
        library.Reader.GetRootFoldersWithCountsAsync(Arg.Any<CancellationToken>()).Returns(new[] { new FolderSummary(server, 1, IsServer: true) });
        var musicServers = Substitute.For<IMusicServerService>();
        musicServers.SyncAsync(server.Id, Arg.Any<IProgress<ScanProgress>?>(), Arg.Any<CancellationToken>()).Returns(
            Task.FromException(new MusicServerException(MusicServerError.SignInFailed, "rejected")),
            Task.CompletedTask);
        var ui = Substitute.For<IUIService>();
        ui.ShowConfirmationDialogAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<bool>()).Returns(true);
        ui.ShowMusicServerSignInDialogAsync(server.Id).Returns(true);
        using var viewModel = CreateViewModel(library, ui, musicServers);
        await viewModel.LoadFoldersCommand.ExecuteAsync(null);

        await viewModel.SyncServerCommand.ExecuteAsync(server.Id);

        await ui.Received(1).ShowMusicServerSignInDialogAsync(server.Id);
        await musicServers.Received(2).SyncAsync(server.Id, Arg.Any<IProgress<ScanProgress>?>(), Arg.Any<CancellationToken>());
        await ui.DidNotReceiveWithAnyArgs().ShowMessageDialogAsync(default!, default!);
    }

    [Fact]
    public async Task SyncServer_Unreachable_SaysWhichServerCouldntBeReached()
    {
        var library = new LibraryFakes();
        var server = new Folder { Name = "Living Room", Path = "nagi-server:0123" };
        library.Reader.GetRootFoldersWithCountsAsync(Arg.Any<CancellationToken>()).Returns(new[] { new FolderSummary(server, 1, IsServer: true) });
        var musicServers = Substitute.For<IMusicServerService>();
        musicServers.SyncAsync(server.Id, Arg.Any<IProgress<ScanProgress>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new MusicServerException(MusicServerError.Unreachable, "timed out")));
        var ui = Substitute.For<IUIService>();
        using var viewModel = CreateViewModel(library, ui, musicServers);
        await viewModel.LoadFoldersCommand.ExecuteAsync(null);

        await viewModel.SyncServerCommand.ExecuteAsync(server.Id);

        await ui.Received(1).ShowMessageDialogAsync(Strings.Generic_Error, string.Format(Strings.MusicServer_Error_Unreachable_Format, "Living Room"));
        viewModel.IsAnyOperationInProgress.Should().BeFalse();
    }

    private static FolderViewModel CreateViewModel(LibraryFakes library, IUIService ui, IMusicServerService? musicServers = null) =>
        new(library.Reader, library.Writer, library.Scanner, musicServers ?? Substitute.For<IMusicServerService>(),
            new GlobalOperationViewModel(), Substitute.For<IMusicPlaybackService>(), Substitute.For<INavigationService>(), ui,
            new ImmediateDispatcher(), NullLogger<FolderViewModel>.Instance);
}
