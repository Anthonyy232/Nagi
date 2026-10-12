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
        var library = Substitute.For<ILibraryFake>();
        var response = new TaskCompletionSource<IEnumerable<FolderSummary>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        library.GetRootFoldersWithCountsAsync(Arg.Any<CancellationToken>()).Returns(call =>
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
            library, library, library, NullLogger<PlayerViewModel>.Instance, Substitute.For<IUIService>());
        using var viewModel = new FolderViewModel(library, library, library, player, playback,
            Substitute.For<INavigationService>(), dispatcher, NullLogger<FolderViewModel>.Instance);
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
}
