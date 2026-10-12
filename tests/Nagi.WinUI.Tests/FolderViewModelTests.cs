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

            response.SetResult(new[] { new FolderSummary(new Folder { Name = "Stale folder" }, 2) });
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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DeleteFolder_RemovesOnlyAfterConfirmation(bool confirmed)
    {
        var library = new LibraryFakes();
        var folder = new Folder { Name = "Music", Path = @"C:Music" };
        library.Reader.GetRootFoldersWithCountsAsync(Arg.Any<CancellationToken>()).Returns(new[] { new FolderSummary(folder, 1) });
        library.Writer.RemoveFolderAsync(folder.Id).Returns(true);
        var ui = Substitute.For<IUIService>();
        ui.ShowConfirmationDialogAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<bool>()).Returns(confirmed);
        using var viewModel = CreateViewModel(library, ui);

        await viewModel.LoadFoldersCommand.ExecuteAsync(null);

        await viewModel.DeleteFolderCommand.ExecuteAsync(folder.Id);

        await library.Writer.Received(confirmed ? 1 : 0).RemoveFolderAsync(folder.Id);
    }

    private static FolderViewModel CreateViewModel(LibraryFakes library, IUIService ui) =>
        new(library.Reader, library.Writer, library.Scanner, new GlobalOperationViewModel(),
            Substitute.For<IMusicPlaybackService>(), Substitute.For<INavigationService>(), ui, new ImmediateDispatcher(),
            NullLogger<FolderViewModel>.Instance);
}
