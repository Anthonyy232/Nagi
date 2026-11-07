using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Nagi.Core.Library;
using Nagi.Core.Platform;
using Nagi.Core.Playback;
using Nagi.Core.Scrobbling;
using Nagi.Core.Settings;
using Nagi.Core.Tests.Utils;
using NSubstitute;
using Xunit;

namespace Nagi.Core.Tests.Scrobbling;

public sealed class ScrobbleCoordinatorTests : IDisposable
{
    private readonly ICredentialLockerService _credentials = Substitute.For<ICredentialLockerService>();
    private readonly SettingsService _settingsService;
    private readonly IMusicPlaybackService _playback = Substitute.For<IMusicPlaybackService>();
    private readonly FakeTimeProvider _time = new();
    private readonly List<ScrobbleCoordinator> _coordinators = [];

    public ScrobbleCoordinatorTests()
    {
        _credentials.SaveCredential(default!, default!, default!).ReturnsForAnyArgs(true);
        _settingsService = TestSettings.Create(credentials: _credentials);
    }

    public void Dispose()
    {
        foreach (var coordinator in _coordinators) coordinator.Dispose();
    }

    [Fact]
    public async Task Start_SendsWhatIsPendingInTheBackground()
    {
        var submitter = new FakeSubmitter { Block = true };
        var coordinator = Create(submitter);

        coordinator.Start();
        _time.Advance(ScrobbleCoordinator.CoalesceDelay);

        await submitter.WaitForRunsAsync(1);
    }

    [Fact]
    public async Task OneDestinationFailing_DoesNotBlockTheOthers()
    {
        // Network failures are handled inside each destination; what escapes is unexpected, such as a database error.
        var failing = new FakeSubmitter { Failure = new InvalidOperationException("database unavailable") };
        var healthy = new FakeSubmitter();
        Create(failing, healthy).Start();

        _time.Advance(ScrobbleCoordinator.CoalesceDelay);

        await healthy.WaitForRunsAsync(1);
    }

    [Fact]
    public async Task Dispose_CancelsARunningSubmission()
    {
        var submitter = new FakeSubmitter { Block = true };
        var coordinator = Create(submitter);
        coordinator.Start();
        _time.Advance(ScrobbleCoordinator.CoalesceDelay);
        await submitter.WaitForRunsAsync(1);

        coordinator.Dispose();

        submitter.LastToken.IsCancellationRequested.Should().BeTrue();
    }

    [Fact]
    public async Task ConnectingAndDisconnecting_RunsOnlyTheAffectedDestinationOnce()
    {
        var lastFm = new FakeSubmitter
        {
            Settings = [CoreSettings.LastFmScrobbling, CoreSettings.LastFmNowPlaying],
            Account = ScrobblingAccount.LastFm
        };
        var listenBrainz = new FakeSubmitter
        {
            Settings = [CoreSettings.ListenBrainzScrobbling, CoreSettings.ListenBrainzEnabledSinceUtc],
            Account = ScrobblingAccount.ListenBrainz
        };
        var song = new Song { Title = "Breakwater" };
        _playback.CurrentTrack.Returns(song);
        _playback.CurrentListenHistoryId.Returns(7L);
        Create(lastFm, listenBrainz).Start();
        _time.Advance(ScrobbleCoordinator.CoalesceDelay);
        await Task.WhenAll(lastFm.WaitForRunsAsync(1), listenBrainz.WaitForRunsAsync(1));
        var accounts = new ScrobblingAccounts(_settingsService);

        await accounts.ConnectLastFmAsync("user", "session");
        _time.Advance(ScrobbleCoordinator.CoalesceDelay);
        await lastFm.WaitForRunsAsync(2);
        await accounts.ConnectListenBrainzAsync("token");
        _time.Advance(ScrobbleCoordinator.CoalesceDelay);
        await listenBrainz.WaitForRunsAsync(2);
        await accounts.DisconnectLastFmAsync();
        _time.Advance(ScrobbleCoordinator.CoalesceDelay);
        await lastFm.WaitForRunsAsync(3);

        lastFm.Runs.Should().Be(3);
        lastFm.Announcements.Should().Be(2);
        listenBrainz.Runs.Should().Be(2);
        listenBrainz.Announcements.Should().Be(1);
    }

    [Fact]
    public async Task RequestsWhileARunWaits_JoinIt()
    {
        var submitter = new FakeSubmitter();
        Create(submitter).Start();

        for (var i = 0; i < 3; i++)
            _playback.ScrobbleEligibilityReached += Raise.Event<Action<Song, long, DateTime>>(new Song(), 1L, DateTime.UtcNow);
        _time.Advance(ScrobbleCoordinator.CoalesceDelay);
        await submitter.WaitForRunsAsync(1);
        _playback.ScrobbleEligibilityReached += Raise.Event<Action<Song, long, DateTime>>(new Song(), 2L, DateTime.UtcNow);
        _time.Advance(ScrobbleCoordinator.CoalesceDelay);
        await submitter.WaitForRunsAsync(2);

        submitter.Runs.Should().Be(2);
    }

    [Fact]
    public async Task Dispose_StopsReactingToChanges()
    {
        var submitter = new FakeSubmitter { Settings = [CoreSettings.LastFmScrobbling], Account = ScrobblingAccount.ListenBrainz };
        var coordinator = Create(submitter);
        coordinator.Start();
        coordinator.Dispose();

        _settingsService.Set(CoreSettings.LastFmScrobbling, true);
        await _settingsService.SaveListenBrainzUserTokenAsync("token");
        _playback.ScrobbleEligibilityReached += Raise.Event<Action<Song, long, DateTime>>(new Song(), 1L, DateTime.UtcNow);
        _time.Advance(TimeSpan.FromHours(1));

        submitter.Runs.Should().Be(0);
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
        var submitter = new FakeSubmitter();
        Create(submitter).Start();
        _time.Advance(ScrobbleCoordinator.CoalesceDelay);
        await submitter.WaitForRunsAsync(1);

        _time.Advance(TimeSpan.FromMinutes(14));
        submitter.Runs.Should().Be(1);
        _time.Advance(TimeSpan.FromMinutes(1));
        _time.Advance(ScrobbleCoordinator.CoalesceDelay);
        await submitter.WaitForRunsAsync(2);
        _time.Advance(TimeSpan.FromMinutes(15));
        _time.Advance(ScrobbleCoordinator.CoalesceDelay);
        await submitter.WaitForRunsAsync(3);
    }

    private ScrobbleCoordinator Create(params IListenSubmitter[] submitters)
    {
        var coordinator = new ScrobbleCoordinator(_playback, submitters, _settingsService,
            NullLogger<ScrobbleCoordinator>.Instance, _time);
        _coordinators.Add(coordinator);
        return coordinator;
    }

    private sealed class FakeSubmitter : IListenSubmitter
    {
        private readonly SemaphoreSlim _runs = new(0);
        private int _runCount;
        private int _announcements;

        public bool Block { get; init; }
        public Exception? Failure { get; init; }
        public CancellationToken LastToken { get; private set; }
        public int Runs => Volatile.Read(ref _runCount);
        public int Announcements => Volatile.Read(ref _announcements);

        public string Name => "fake";
        public IReadOnlyCollection<Setting> Settings { get; init; } = [];
        public ScrobblingAccount? Account { get; init; }

        public Task UpdateNowPlayingAsync(Song song)
        {
            Interlocked.Increment(ref _announcements);
            return Task.CompletedTask;
        }

        public Task SubmitPendingAsync(CancellationToken cancellationToken)
        {
            LastToken = cancellationToken;
            Interlocked.Increment(ref _runCount);
            _runs.Release();
            if (Failure is not null) return Task.FromException(Failure);
            return Block ? Task.Delay(Timeout.Infinite, cancellationToken) : Task.CompletedTask;
        }

        public async Task WaitForRunsAsync(int count)
        {
            while (Runs < count)
                (await _runs.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();
        }
    }
}
