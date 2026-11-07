using Microsoft.Extensions.Logging;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Implementations;
using Nagi.Core.Services.Implementations.Presence;
using Nagi.Core.Settings;
using Nagi.Core.Tests.Utils;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Nagi.Core.Tests;

public class LastFmPresenceServiceTests : IAsyncDisposable
{
    private readonly ILastFmScrobblerService _scrobblerService;
    private readonly ILibraryWriter _libraryWriter;
    private readonly SettingsService _settingsService;
    private readonly ILogger<LastFmPresenceService> _logger;
    private readonly LastFmPresenceService _service;

    public LastFmPresenceServiceTests()
    {
        _scrobblerService = Substitute.For<ILastFmScrobblerService>();
        _libraryWriter = Substitute.For<ILibraryWriter>();
        _settingsService = TestSettings.Create();
        _logger = Substitute.For<ILogger<LastFmPresenceService>>();

        _settingsService.Set(CoreSettings.LastFmNowPlaying, true);
        _settingsService.Set(CoreSettings.LastFmScrobbling, true);
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
        _settingsService.Set(CoreSettings.LastFmScrobbling, false);
        await _service.OnTrackEligibleForScrobblingAsync(song, 1, DateTime.UnixEpoch);
        await _scrobblerService.DidNotReceive().ScrobbleAsync(Arg.Any<Song>(), Arg.Any<DateTime>());

        _settingsService.Set(CoreSettings.LastFmScrobbling, true);
        await _service.OnTrackEligibleForScrobblingAsync(song, 1, DateTime.UnixEpoch);
        await _scrobblerService.Received(1).ScrobbleAsync(song, DateTime.UnixEpoch);
    }

    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 0)]
    public async Task OnTrackChangedAsync_UpdatesNowPlayingOnlyWhenEnabled(bool nowPlayingEnabled, int expectedCalls)
    {
        _settingsService.Set(CoreSettings.LastFmNowPlaying, nowPlayingEnabled);
        await _service.InitializeAsync();

        var song = new Song { Title = "Test Song" };
        await _service.OnTrackChangedAsync(song, 1L);

        await _scrobblerService.Received(expectedCalls).UpdateNowPlayingAsync(song);
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
}
