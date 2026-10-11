using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;
using Nagi.WinUI.Models;
using Nagi.WinUI.Services.Implementations;
using Nagi.WinUI.ViewModels;
using NSubstitute;
using Xunit;

namespace Nagi.WinUI.Tests;

public partial class PlayerViewModelTests
{

    [Fact]
    public async Task FavoriteWriteFinishingAfterTrackChange_DoesNotMarkTheNewTrack()
    {
        var original = new Song { Title = "Original", FolderId = Guid.NewGuid() };
        var next = new Song { Title = "Next" };
        _playback.CurrentTrack.Returns(original);
        _playback.TrackChanged += Raise.Event<Action>();
        var write = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _library.SetSongsLovedAsync(Arg.Any<IEnumerable<Guid>>(), true).Returns(write.Task);

        var favoriting = _viewModel.ToggleFavoriteCommand.ExecuteAsync(null);
        _playback.CurrentTrack.Returns(next);
        _playback.TrackChanged += Raise.Event<Action>();
        write.SetResult(true);
        await favoriting;

        _viewModel.IsCurrentTrackFavorite.Should().BeFalse();
        await _library.Received(1).SetSongsLovedAsync(Arg.Is<IEnumerable<Guid>>(ids => ids.Single() == original.Id), true);
    }
}
