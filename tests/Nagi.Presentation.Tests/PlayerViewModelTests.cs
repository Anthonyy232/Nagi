using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;
using Nagi.Presentation.Models;
using Nagi.Presentation.Services.Abstractions;
using Nagi.Presentation.ViewModels;
using NSubstitute;
using Xunit;

namespace Nagi.Presentation.Tests;

public partial class PlayerViewModelTests : IDisposable
{
    private readonly IMusicPlaybackService _playback = Substitute.For<IMusicPlaybackService>();
    private readonly IUISettingsService _settings = Substitute.For<IUISettingsService>();
    private readonly IDispatcherService _dispatcher = Substitute.For<IDispatcherService>();
    private readonly PlayerViewModel _viewModel;
    private readonly LibraryFakes _library = new LibraryFakes();

    public PlayerViewModelTests()
    {
        _dispatcher.HasThreadAccess.Returns(true);
        _dispatcher.EnqueueAsync(Arg.Any<Func<Task>>()).Returns(call => call.Arg<Func<Task>>()());
        _playback.PlaybackQueue.Returns(Array.Empty<Guid>());
        _settings.GetPlayerButtonSettingsAsync().Returns(new List<PlayerButtonSetting>
        {
            new() { Id = "Volume", DisplayName = "Volume", IsEnabled = true }
        });
        _settings.GetQueueDisplaySettingsAsync().Returns(new QueueDisplaySettings());
        _viewModel = new PlayerViewModel(_playback, Substitute.For<IMusicNavigationService>(),
            _dispatcher, _settings, Substitute.For<IWindowService>(),
            _library.Reader, _library.Writer, _library.Scanner, NullLogger<PlayerViewModel>.Instance, Substitute.For<IUIService>());
    }

    public void Dispose() => _viewModel.Cleanup();

    [Fact]
    public void ServiceVolumeChanges_UpdateBothIconsAndMuteTooltip()
    {
        var volumeButton = _viewModel.MainTransportButtons.Single();
        _playback.Volume.Returns(.9);
        _playback.VolumeStateChanged += Raise.Event<Action>();
        _viewModel.VolumeIcon.Should().Be(AppIcon.VolumeHigh);
        volumeButton.DynamicIcon.Should().Be(_viewModel.VolumeIcon);

        _playback.Volume.Returns(0);
        _playback.VolumeStateChanged += Raise.Event<Action>();
        var muteTooltip = volumeButton.DynamicToolTip;
        _playback.IsMuted.Returns(true);
        _playback.VolumeStateChanged += Raise.Event<Action>();

        _viewModel.VolumeIcon.Should().Be(AppIcon.Mute);
        volumeButton.DynamicIcon.Should().Be(_viewModel.VolumeIcon);
        volumeButton.DynamicToolTip.Should().NotBe(muteTooltip);
        _playback.DidNotReceive().SetVolumeAsync(Arg.Any<double>());
    }

    [Fact]
    public async Task HiddenQueuePreview_DefersFetchUntilOpenedAndRefreshesOnReopen()
    {
        var first = new Song { Title = "First" };
        var next = new Song { Title = "Next" };
        _playback.GetQueueTracksAsync(Arg.Any<IEnumerable<Guid>>())
            .Returns(new Dictionary<Guid, Song> { [first.Id] = first, [next.Id] = next });
        _playback.PlaybackQueue.Returns(new[] { first.Id });
        _playback.QueueChanged += Raise.Event<Action>();
        _playback.TrackChanged += Raise.Event<Action>();
        await _playback.DidNotReceive().GetQueueTracksAsync(Arg.Any<IEnumerable<Guid>>());

        _viewModel.IsQueueViewVisible = true;
        _viewModel.CurrentQueue.Should().ContainSingle().Which.Should().BeSameAs(first);
        _viewModel.IsQueueViewVisible = false;
        _playback.ClearReceivedCalls();
        _playback.PlaybackQueue.Returns(new[] { next.Id });
        _playback.QueueChanged += Raise.Event<Action>();
        await _playback.DidNotReceive().GetQueueTracksAsync(Arg.Any<IEnumerable<Guid>>());

        _viewModel.IsQueueViewVisible = true;
        _viewModel.CurrentQueue.Should().ContainSingle().Which.Should().BeSameAs(next);
    }

    [Theory]
    [InlineData(false, 4, 3, 1, 4)]
    [InlineData(true, 4, 3, 1, 4)]
    [InlineData(false, 150, 140, 10, 100)]
    [InlineData(false, 4, -1, 4, 4)]
    public void RepeatChanges_RefreshBoundedQueuePreview(bool shuffle, int count, int index, int linearCount, int repeatCount)
    {
        var songs = Enumerable.Range(0, count).Select(i => new Song { Title = i.ToString() }).ToArray();
        var ids = songs.Select(song => song.Id).ToArray();
        _playback.PlaybackQueue.Returns(ids);
        _playback.ShuffledQueue.Returns(ids.Reverse().ToArray());
        _playback.IsShuffleEnabled.Returns(shuffle);
        _playback.CurrentQueueIndex.Returns(index);
        _playback.CurrentShuffledIndex.Returns(index);
        _playback.GetQueueTracksAsync(Arg.Any<IEnumerable<Guid>>()).Returns(songs.ToDictionary(song => song.Id));
        _viewModel.IsQueueViewVisible = true;
        _playback.QueueChanged += Raise.Event<Action>();
        _viewModel.CurrentQueue.Should().HaveCount(linearCount);

        _playback.CurrentRepeatMode.Returns(RepeatMode.RepeatAll);
        _playback.RepeatModeChanged += Raise.Event<Action>();

        _viewModel.CurrentQueue.Should().HaveCount(repeatCount).And.OnlyHaveUniqueItems();
        var first = Math.Max(index, 0);
        _viewModel.CurrentQueue[0].Should().Be(songs[shuffle ? count - 1 - first : first]);
        if (index > 0)
            _viewModel.CurrentQueue[linearCount].Should().Be(songs[shuffle ? count - 1 : 0]);

        _playback.CurrentRepeatMode.Returns(RepeatMode.Off);
        _playback.RepeatModeChanged += Raise.Event<Action>();
        _viewModel.CurrentQueue.Should().HaveCount(linearCount);
    }

    [Fact]
    public void NestedServiceEvent_DoesNotAllowOuterUpdateToSeekBack()
    {
        _playback.CurrentPosition.Returns(TimeSpan.FromSeconds(10));
        _playback.PositionChanged += Raise.Event<Action>();
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PlayerViewModel.SongTitle))
                _playback.VolumeStateChanged += Raise.Event<Action>();
        };
        _playback.CurrentTrack.Returns(new Song { Title = "Next track" });

        _playback.TrackChanged += Raise.Event<Action>();

        _viewModel.CurrentPosition.Should().Be(0);
        _playback.DidNotReceive().SeekAsync(Arg.Any<TimeSpan>());
    }

}
