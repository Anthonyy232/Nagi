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

        // Default: scrobbling and now-playing both enabled
        _settingsService.GetLastFmNowPlayingEnabledAsync().Returns(true);
        _settingsService.GetLastFmScrobblingEnabledAsync().Returns(true);
        _scrobblerService.ScrobbleAsync(Arg.Any<Song>(), Arg.Any<DateTime>()).Returns(true);

        _service = new LastFmPresenceService(
            _scrobblerService,
            _libraryWriter,
            _settingsService,
            _logger);
    }

    public async ValueTask DisposeAsync() => await _service.DisposeAsync();

    // -------------------------------------------------------------------------
    // InitializeAsync
    // -------------------------------------------------------------------------

    [Fact]
    public async Task InitializeAsync_LoadsSettingsAndSubscribesToChanges()
    {
        await _service.InitializeAsync();

        await _settingsService.Received(1).GetLastFmNowPlayingEnabledAsync();
        await _settingsService.Received(1).GetLastFmScrobblingEnabledAsync();
    }

    // -------------------------------------------------------------------------
    // OnTrackChangedAsync
    // -------------------------------------------------------------------------

    [Fact]
    public async Task OnTrackChangedAsync_WhenNowPlayingEnabled_UpdatesNowPlaying()
    {
        await _service.InitializeAsync(); // loads settings (NowPlaying = true)

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

        // Should not throw
        await _service.OnTrackChangedAsync(new Song { Title = "Song" }, 1L);
    }

    // -------------------------------------------------------------------------
    // OnTrackEligibleForScrobblingAsync
    // -------------------------------------------------------------------------

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

        // Should not throw
        await _service.OnTrackEligibleForScrobblingAsync(
            new Song { Title = "Song" }, 1L, DateTime.UnixEpoch);
    }

    // -------------------------------------------------------------------------
    // DisposeAsync
    // -------------------------------------------------------------------------

    [Fact]
    public async Task DisposeAsync_UnsubscribesFromSettingsEvent()
    {
        await _service.InitializeAsync();
        await _service.DisposeAsync();

        // After disposal, raising the event should not trigger UpdateLocalSettings again
        _settingsService.ClearReceivedCalls();
        _settingsService.LastFmSettingsChanged += Raise.Event<Action>();
        await Task.Delay(50);

        await _settingsService.DidNotReceive().GetLastFmNowPlayingEnabledAsync();
    }
}
