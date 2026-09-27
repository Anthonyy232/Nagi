using FluentAssertions;
using Microsoft.Extensions.Logging;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Implementations.Presence;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Nagi.Core.Tests;

public class LastFmPresenceServiceTests : IAsyncDisposable
{
    private readonly ILastFmScrobblerService _scrobblerService;
    private readonly ILibraryWriter _libraryWriter;
    private readonly ISettingsService _settingsService;
    private readonly ILogger<LastFmPresenceService> _logger;
    private readonly LastFmPresenceService _service;

    public LastFmPresenceServiceTests()
    {
        _scrobblerService = Substitute.For<ILastFmScrobblerService>();
        _libraryWriter = Substitute.For<ILibraryWriter>();
        _settingsService = Substitute.For<ISettingsService>();
        _logger = Substitute.For<ILogger<LastFmPresenceService>>();

        _settingsService.GetLastFmNowPlayingEnabledAsync().Returns(true);
        _settingsService.GetLastFmScrobblingEnabledAsync().Returns(true);
        _scrobblerService.ScrobbleAsync(Arg.Any<Song>(), Arg.Any<DateTime>()).Returns(true);

        _service = new LastFmPresenceService(
            _scrobblerService,
            _libraryWriter,
            _settingsService,
            _logger);
    }

    public ValueTask DisposeAsync() => _service.DisposeAsync();

    [Fact]
    public async Task SettingsChange_UpdatesScrobblingWithoutReinitialization()
    {
        await _service.InitializeAsync();
        var song = new Song { Title = "Song" };
        _settingsService.GetLastFmScrobblingEnabledAsync().Returns(false);
        _settingsService.LastFmSettingsChanged += Raise.Event<Action>();
        await _service.OnTrackEligibleForScrobblingAsync(song, 1, DateTime.UnixEpoch);
        await _scrobblerService.DidNotReceive().ScrobbleAsync(Arg.Any<Song>(), Arg.Any<DateTime>());

        _settingsService.GetLastFmScrobblingEnabledAsync().Returns(true);
        _settingsService.LastFmSettingsChanged += Raise.Event<Action>();
        await _service.OnTrackEligibleForScrobblingAsync(song, 1, DateTime.UnixEpoch);
        await _scrobblerService.Received(1).ScrobbleAsync(song, DateTime.UnixEpoch);
    }

    [Fact]
    public async Task OnTrackChangedAsync_WhenNowPlayingEnabled_UpdatesNowPlaying()
    {
        await _service.InitializeAsync();

        var song = new Song { Title = "Test Song" };
        await _service.OnTrackChangedAsync(song, 1L);

        await _scrobblerService.Received(1).UpdateNowPlayingAsync(song);
    }

    [Fact]
    public async Task OnTrackChangedAsync_WhenNowPlayingDisabled_DoesNotUpdateNowPlaying()
    {
        _settingsService.GetLastFmNowPlayingEnabledAsync().Returns(false);
        await _service.InitializeAsync();

        await _service.OnTrackChangedAsync(new Song { Title = "Song" }, 1L);

        await _scrobblerService.DidNotReceive().UpdateNowPlayingAsync(Arg.Any<Song>());
    }

    [Fact]
    public async Task OnTrackChangedAsync_WhenNowPlayingThrows_DoesNotPropagateException()
    {
        await _service.InitializeAsync();
        _scrobblerService.UpdateNowPlayingAsync(Arg.Any<Song>()).ThrowsAsync(new HttpRequestException("network error"));

        await _service.OnTrackChangedAsync(new Song { Title = "Song" }, 1L);
    }

    [Fact]
    public async Task OnTrackEligibleForScrobblingAsync_WhenScrobblingEnabled_ScrobblesAndMarksAsScrobbled()
    {
        await _service.InitializeAsync();

        var song = new Song { Title = "Song" };
        await _service.OnTrackEligibleForScrobblingAsync(song, 42L, DateTime.UnixEpoch);

        await _scrobblerService.Received(1).ScrobbleAsync(song, DateTime.UnixEpoch);
        await _libraryWriter.Received(1).MarkListenAsScrobbledAsync(42L);
    }

    [Fact]
    public async Task OnTrackEligibleForScrobblingAsync_WhenScrobblingDisabled_DoesNotScrobble()
    {
        _settingsService.GetLastFmScrobblingEnabledAsync().Returns(false);
        await _service.InitializeAsync();

        await _service.OnTrackEligibleForScrobblingAsync(
            new Song { Title = "Song" }, 1L, DateTime.UnixEpoch);

        await _scrobblerService.DidNotReceive().ScrobbleAsync(Arg.Any<Song>(), Arg.Any<DateTime>());
    }

    [Fact]
    public async Task OnTrackEligibleForScrobblingAsync_WhenScrobbleFails_DoesNotMarkAsScrobbled()
    {
        _scrobblerService.ScrobbleAsync(Arg.Any<Song>(), Arg.Any<DateTime>()).Returns(false);
        await _service.InitializeAsync();

        await _service.OnTrackEligibleForScrobblingAsync(
            new Song { Title = "Song" }, 1L, DateTime.UnixEpoch);

        await _libraryWriter.DidNotReceive().MarkListenAsScrobbledAsync(Arg.Any<long>());
    }

    [Fact]
    public async Task OnTrackEligibleForScrobblingAsync_WhenScrobblerThrows_DoesNotPropagateException()
    {
        _scrobblerService.ScrobbleAsync(Arg.Any<Song>(), Arg.Any<DateTime>()).ThrowsAsync(new HttpRequestException());
        await _service.InitializeAsync();

        await _service.OnTrackEligibleForScrobblingAsync(
            new Song { Title = "Song" }, 1L, DateTime.UnixEpoch);

        await _libraryWriter.DidNotReceive().MarkListenAsScrobbledAsync(Arg.Any<long>());
    }

    [Fact]
    public async Task OnTrackProgressAsync_DoesNotSubmitWithoutEligibilitySignal()
    {
        await _service.InitializeAsync();
        var song = new Song { Title = "Song", Duration = TimeSpan.FromMinutes(3) };
        await _service.OnTrackChangedAsync(song, 1);

        await _service.OnTrackProgressAsync(TimeSpan.FromMinutes(2), song.Duration);

        await _scrobblerService.DidNotReceive().ScrobbleAsync(Arg.Any<Song>(), Arg.Any<DateTime>());
        await _libraryWriter.DidNotReceive().MarkListenAsEligibleForScrobblingAsync(Arg.Any<long>());
        await _libraryWriter.DidNotReceive().MarkListenAsScrobbledAsync(Arg.Any<long>());
    }

    [Fact]
    public async Task DisposeAsync_UnsubscribesFromSettingsEvent()
    {
        await _service.InitializeAsync();
        await _service.DisposeAsync();

        _settingsService.ClearReceivedCalls();
        _settingsService.LastFmSettingsChanged += Raise.Event<Action>();
        await _settingsService.DidNotReceive().GetLastFmNowPlayingEnabledAsync();
    }
}
