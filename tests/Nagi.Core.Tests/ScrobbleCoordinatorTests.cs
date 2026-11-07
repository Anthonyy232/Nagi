using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Nagi.Core.Library;
using Nagi.Core.Playback;
using Nagi.Core.Scrobbling;
using Nagi.Core.Services.Implementations;
using Nagi.Core.Settings;
using Nagi.Core.Tests.Utils;
using NSubstitute;
using Xunit;

namespace Nagi.Core.Tests;

public sealed class ScrobbleCoordinatorTests : IDisposable
{
    private readonly SettingsService _settingsService = TestSettings.Create();
    private readonly IMusicPlaybackService _playback = Substitute.For<IMusicPlaybackService>();
    private readonly FakeTimeProvider _time = new();
    private readonly List<ScrobbleCoordinator> _coordinators = [];

    public void Dispose()
    {
        foreach (var coordinator in _coordinators) coordinator.Dispose();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SubmitPendingAsync_OneDestinationFailing_DoesNotBlockTheOthers(bool timeout)
    {
        var failing = Substitute.For<IListenSubmitter>();
        failing.SubmitPendingAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException(timeout ? new TaskCanceledException("Request timed out") : new HttpRequestException("boom")));
        var healthy = Substitute.For<IListenSubmitter>();
        var coordinator = Create(failing, healthy);

        (await coordinator.SubmitPendingAsync()).Should().BeFalse();

        await healthy.Received(1).SubmitPendingAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SubmitPendingAsync_WhenCancelled_DoesNotCallDestinations()
    {
        var submitter = Substitute.For<IListenSubmitter>();
        var coordinator = Create(submitter);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await coordinator.SubmitPendingAsync(cancellation.Token);

        await submitter.DidNotReceiveWithAnyArgs().SubmitPendingAsync(default);
    }

    [Fact]
    public async Task Dispose_CancelsARunningSubmission()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var submitter = Substitute.For<IListenSubmitter>();
        submitter.SubmitPendingAsync(Arg.Any<CancellationToken>()).Returns(async call =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, call.Arg<CancellationToken>());
        });
        var coordinator = Create(submitter);
        var submitting = coordinator.SubmitPendingAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        coordinator.Dispose();

        await FluentActions.Awaiting(() => submitting.WaitAsync(TimeSpan.FromSeconds(5)))
            .Should().ThrowAsync<OperationCanceledException>();
    }

    [Theory]
    [InlineData("lastfm")]
    [InlineData("listenbrainz")]
    [InlineData("credentials")]
    public async Task ScrobblingChange_SubmitsAtOnce(string change)
    {
        var submitter = Substitute.For<IListenSubmitter>();
        Create(submitter).Start();

        if (change == "lastfm") _settingsService.Set(CoreSettings.LastFmScrobbling, true);
        else if (change == "listenbrainz") _settingsService.Set(CoreSettings.ListenBrainzScrobbling, true);
        else await _settingsService.SaveListenBrainzUserTokenAsync("token");

        await submitter.Received().SubmitPendingAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Dispose_StopsReactingToChanges()
    {
        var submitter = Substitute.For<IListenSubmitter>();
        var coordinator = Create(submitter);
        coordinator.Start();
        coordinator.Dispose();

        _settingsService.Set(CoreSettings.LastFmScrobbling, true);
        await _settingsService.SaveListenBrainzUserTokenAsync("token");
        _playback.ScrobbleEligibilityReached += Raise.Event<Action<Song, long, DateTime>>(new Song(), 1L, DateTime.UtcNow);

        await submitter.DidNotReceiveWithAnyArgs().SubmitPendingAsync(default);
    }

    [Fact]
    public async Task ListenReachingTheThreshold_IsSubmittedAtOnce()
    {
        var submitter = Substitute.For<IListenSubmitter>();
        Create(submitter).Start();

        _playback.ScrobbleEligibilityReached += Raise.Event<Action<Song, long, DateTime>>(new Song(), 1L, DateTime.UtcNow);

        await submitter.Received(1).SubmitPendingAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TrackChange_AnnouncesEachListenOnceAndNotARestoredTrack()
    {
        var submitter = Substitute.For<IListenSubmitter>();
        Create(submitter).Start();
        var song = new Song { Title = "Breakwater" };
        _playback.CurrentTrack.Returns(song);

        _playback.TrackChanged += Raise.Event<Action>();
        _playback.CurrentListenHistoryId.Returns(7L);
        _playback.TrackChanged += Raise.Event<Action>();
        _playback.TrackChanged += Raise.Event<Action>();
        _playback.CurrentListenHistoryId.Returns(8L);
        _playback.TrackChanged += Raise.Event<Action>();

        await submitter.Received(2).UpdateNowPlayingAsync(song);
    }

    [Fact]
    public async Task Retry_RunsEveryQuarterHour()
    {
        var runs = new SemaphoreSlim(0);
        var submitter = Substitute.For<IListenSubmitter>();
        submitter.SubmitPendingAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            runs.Release();
            return Task.CompletedTask;
        });
        Create(submitter).Start();

        _time.Advance(TimeSpan.FromMinutes(14));
        (await runs.WaitAsync(TimeSpan.FromMilliseconds(100))).Should().BeFalse();
        _time.Advance(TimeSpan.FromMinutes(1));
        (await runs.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();
        _time.Advance(TimeSpan.FromMinutes(15));
        (await runs.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();
    }

    private ScrobbleCoordinator Create(params IListenSubmitter[] submitters)
    {
        var coordinator = new ScrobbleCoordinator(_playback, submitters, _settingsService,
            NullLogger<ScrobbleCoordinator>.Instance, _time);
        _coordinators.Add(coordinator);
        return coordinator;
    }
}
