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
        var playback = Substitute.For<IMusicPlaybackService>();
        var dispatcher = new ImmediateDispatcher();
        var settings = Substitute.For<IUISettingsService>();
        settings.GetPlayerButtonSettingsAsync().Returns(new List<PlayerButtonSetting>());
        settings.GetQueueDisplaySettingsAsync().Returns(new QueueDisplaySettings());
        var player = new PlayerViewModel(playback, Substitute.For<IMusicNavigationService>(),
            dispatcher, settings, Substitute.For<IWindowService>(),
            library.Reader, library.Writer, library.Scanner, NullLogger<PlayerViewModel>.Instance, Substitute.For<IUIService>());
        using var viewModel = new FolderViewModel(library.Reader, library.Writer, library.Scanner, player, playback,
            Substitute.For<INavigationService>(), Substitute.For<IUIService>(), dispatcher, NullLogger<FolderViewModel>.Instance);
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
            player.Cleanup();
        }
    }

    [Fact]
    public async Task AddFolder_CancelledPickerAddsNothing()
    {
        var library = new LibraryFakes();
        var ui = Substitute.For<IUIService>();
        ui.PickSingleFolderAsync().Returns((string?)null);
        var (viewModel, player) = CreateViewModel(library, ui);
        using (viewModel)
        {
            await viewModel.AddFolderCommand.ExecuteAsync(null);

            await library.Writer.DidNotReceiveWithAnyArgs().AddFolderAsync(default!, default);
        }
        player.Cleanup();
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
        ui.ShowConfirmationDialogAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>()).Returns(confirmed);
        var (viewModel, player) = CreateViewModel(library, ui);
        using (viewModel)
        {
            await viewModel.LoadFoldersCommand.ExecuteAsync(null);

            await viewModel.DeleteFolderCommand.ExecuteAsync(folder.Id);

            await library.Writer.Received(confirmed ? 1 : 0).RemoveFolderAsync(folder.Id);
        }
        player.Cleanup();
    }

    private static (FolderViewModel ViewModel, PlayerViewModel Player) CreateViewModel(LibraryFakes library, IUIService ui)
    {
        var playback = Substitute.For<IMusicPlaybackService>();
        var dispatcher = new ImmediateDispatcher();
        var settings = Substitute.For<IUISettingsService>();
        settings.GetPlayerButtonSettingsAsync().Returns(new List<PlayerButtonSetting>());
        settings.GetQueueDisplaySettingsAsync().Returns(new QueueDisplaySettings());
        var player = new PlayerViewModel(playback, Substitute.For<IMusicNavigationService>(),
            dispatcher, settings, Substitute.For<IWindowService>(),
            library.Reader, library.Writer, library.Scanner, NullLogger<PlayerViewModel>.Instance, Substitute.For<IUIService>());
        var viewModel = new FolderViewModel(library.Reader, library.Writer, library.Scanner, player, playback,
            Substitute.For<INavigationService>(), ui, dispatcher, NullLogger<FolderViewModel>.Instance);
        return (viewModel, player);
    }
}
