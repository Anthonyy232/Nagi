using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Nagi.Core.Library;
using Nagi.Core.Playback;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Implementations;
using Nagi.Presentation.Models;
using Nagi.Presentation.Resources;
using Nagi.Presentation.Services;
using Nagi.Presentation.Services.Abstractions;
using Nagi.Presentation.ViewModels;
using NSubstitute;
using Xunit;

namespace Nagi.Presentation.Tests;

public partial class PlayerViewModelTests : IDisposable
{
    private readonly IMusicPlaybackService _playback = Substitute.For<IMusicPlaybackService>();
    private readonly SettingsService _settings = TestSettings.Create();
    private readonly IDispatcherService _dispatcher = Substitute.For<IDispatcherService>();
    private readonly FakeTimeProvider _time = new();
    private readonly PlayerViewModel _viewModel;
    private readonly LibraryFakes _library = new LibraryFakes();

    public PlayerViewModelTests()
    {
        _dispatcher.HasThreadAccess.Returns(true);
        _dispatcher.EnqueueAsync(Arg.Any<Func<Task>>()).Returns(call => call.Arg<Func<Task>>()());
        _playback.PlayOrder.Returns(Array.Empty<Guid>());
        _settings.Set(AppSettings.PlayerButtons, [new PlayerButtonSetting { Id = "Volume", DisplayName = "Volume", IsEnabled = true }]);
        _viewModel = new PlayerViewModel(_playback, Substitute.For<IMusicNavigationService>(),
            _dispatcher, _settings, Substitute.For<IWindowService>(),
            _library.Reader, _library.Listening, _library.Notifier, NullLogger<PlayerViewModel>.Instance, Substitute.For<IUIService>(), _time);
    }

    public void Dispose() => _viewModel.Cleanup();

    [Fact]
    public async Task OpeningIndicator_ShowsOnlyForASongSlowToOpen()
    {
        _playback.IsOpening.Returns(true);
        _playback.PlaybackStateChanged += Raise.Event<Action>();
        _time.Advance(TimeSpan.FromMilliseconds(250));
        _playback.IsOpening.Returns(false);
        _playback.PlaybackStateChanged += Raise.Event<Action>();
        _time.Advance(TimeSpan.FromMilliseconds(500));
        await _viewModel.PendingOpeningIndicator.WaitAsync(TimeSpan.FromSeconds(5));
        _viewModel.IsOpening.Should().BeFalse("a song that opens at once shouldn't flash the indicator");

        _playback.IsOpening.Returns(true);
        _playback.PlaybackStateChanged += Raise.Event<Action>();
        _time.Advance(TimeSpan.FromMilliseconds(500));
        await _viewModel.PendingOpeningIndicator.WaitAsync(TimeSpan.FromSeconds(5));
        _viewModel.IsOpening.Should().BeTrue();
        _viewModel.PlayPauseButtonToolTip.Should().Be(Strings.Status_Loading);

        _playback.IsOpening.Returns(false);
        _playback.PlaybackStateChanged += Raise.Event<Action>();
        _viewModel.IsOpening.Should().BeFalse();
    }

    [Fact]
    public void ASongRestoredAfterThePlayerIsCreated_ShowsItsDurationBeforeItPlays()
    {
        _playback.CurrentTrack.Returns(new Song { Title = "Restored" });
        _playback.Duration.Returns(TimeSpan.FromSeconds(248));
        _playback.TrackChanged += Raise.Event<Action>();

        _viewModel.TotalDuration.Should().Be(248);
    }

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
        _playback.GetQueueSongsAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, Song> { [first.Id] = first, [next.Id] = next });
        _playback.UpcomingQueue.Returns(new[] { first.Id });
        _playback.QueueChanged += Raise.Event<Action>();
        _playback.TrackChanged += Raise.Event<Action>();
        await _playback.DidNotReceive().GetQueueSongsAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>());

        _viewModel.IsQueueViewVisible = true;
        _viewModel.CurrentQueue.Should().ContainSingle().Which.Should().BeSameAs(first);
        _viewModel.IsQueueViewVisible = false;
        _playback.ClearReceivedCalls();
        _playback.UpcomingQueue.Returns(new[] { next.Id });
        _playback.QueueChanged += Raise.Event<Action>();
        await _playback.DidNotReceive().GetQueueSongsAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>());

        _viewModel.IsQueueViewVisible = true;
        _viewModel.CurrentQueue.Should().ContainSingle().Which.Should().BeSameAs(next);
    }

    [Fact]
    public void QueuePreview_ShowsAtMostAHundredUpcomingSongsAndFollowsRepeatChanges()
    {
        var songs = Enumerable.Range(0, 150).Select(i => new Song { Title = i.ToString() }).ToArray();
        var ids = songs.Select(song => song.Id).ToArray();
        _playback.UpcomingQueue.Returns(ids);
        _playback.GetQueueSongsAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(songs.ToDictionary(song => song.Id));
        _viewModel.IsQueueViewVisible = true;
        _playback.QueueChanged += Raise.Event<Action>();
        _viewModel.CurrentQueue.Should().Equal(songs.Take(100));

        _playback.UpcomingQueue.Returns(ids.Skip(140).ToArray());
        _playback.RepeatModeChanged += Raise.Event<Action>();

        _viewModel.CurrentQueue.Should().Equal(songs.Skip(140));
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

    [Fact]
    public async Task AddFilesToQueue_QueuesDroppedItemsByFileNameNotByFolder()
    {
        IEnumerable<string>? queued = null;
        _playback.AddTransientFilesToQueueAsync(Arg.Do<IEnumerable<string>>(paths => queued = paths.ToList()))
            .Returns(Task.CompletedTask);

        await _viewModel.AddFilesToQueueAsync([@"C:\b\Charlie.mp3", @"C:\a\bravo.mp3", @"C:\c\Alpha"]);

        queued.Should().Equal(@"C:\c\Alpha", @"C:\a\bravo.mp3", @"C:\b\Charlie.mp3");
    }
}
