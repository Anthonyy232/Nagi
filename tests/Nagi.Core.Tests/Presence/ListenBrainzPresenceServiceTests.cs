using Microsoft.Extensions.Logging;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Implementations.Presence;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Nagi.Core.Tests.Presence;

public class ListenBrainzPresenceServiceTests : IAsyncDisposable
{
    private readonly ILibraryWriter _libraryWriter;
    private readonly ILogger<ListenBrainzPresenceService> _logger;
    private readonly IListenBrainzScrobblerService _scrobblerService;
    private readonly ListenBrainzPresenceService _service;
    private readonly ISettingsService _settingsService;

    public ListenBrainzPresenceServiceTests()
    {
        _scrobblerService = Substitute.For<IListenBrainzScrobblerService>();
        _libraryWriter = Substitute.For<ILibraryWriter>();
        _settingsService = Substitute.For<ISettingsService>();
        _logger = Substitute.For<ILogger<ListenBrainzPresenceService>>();

        _service = new ListenBrainzPresenceService(
            _scrobblerService,
            _libraryWriter,
            _settingsService,
            _logger);
    }

    public ValueTask DisposeAsync() => _service.DisposeAsync();

    private async Task InitializeServiceAsync(bool nowPlaying, bool scrobbling)
    {
        _settingsService.GetListenBrainzNowPlayingEnabledAsync().Returns(nowPlaying);
        _settingsService.GetListenBrainzScrobblingEnabledAsync().Returns(scrobbling);
        await _service.InitializeAsync();
    }

    private static Song CreateTestSong(TimeSpan duration)
    {
        return new Song { Id = Guid.NewGuid(), Title = "Test Song", Duration = duration };
    }

    [Fact]
    public async Task OnTrackChangedAsync_WithNowPlayingEnabled_UpdatesNowPlaying()
    {
        await InitializeServiceAsync(true, false);
        var song = CreateTestSong(TimeSpan.FromMinutes(3));

        await _service.OnTrackChangedAsync(song, 1);

        await _scrobblerService.Received(1).UpdateNowPlayingAsync(song);
    }

    [Fact]
    public async Task OnTrackChangedAsync_WithNowPlayingDisabled_DoesNotUpdateNowPlaying()
    {
        await InitializeServiceAsync(false, false);
        var song = CreateTestSong(TimeSpan.FromMinutes(3));

        await _service.OnTrackChangedAsync(song, 1);

        await _scrobblerService.DidNotReceive().UpdateNowPlayingAsync(Arg.Any<Song>());
    }

    [Fact]
    public async Task OnTrackEligibleForScrobblingAsync_WhenScrobblingDisabled_DoesNotSubmit()
    {
        await InitializeServiceAsync(false, false);
        var song = CreateTestSong(TimeSpan.FromMinutes(3));
        await _service.OnTrackChangedAsync(song, 1);

        await _service.OnTrackEligibleForScrobblingAsync(song, 1, DateTime.UnixEpoch);

        await _scrobblerService.DidNotReceive().SubmitListenAsync(Arg.Any<Song>(), Arg.Any<DateTime>());
        await _libraryWriter.DidNotReceive().MarkListenAsSubmittedToListenBrainzAsync(Arg.Any<long>());
    }

    [Fact]
    public async Task OnTrackEligibleForScrobblingAsync_WhenRealtimeSubmitSucceeds_MarksAsSubmitted()
    {
        await InitializeServiceAsync(false, true);
        var song = CreateTestSong(TimeSpan.FromMinutes(3));
        await _service.OnTrackChangedAsync(song, 1);
        _scrobblerService.SubmitListenAsync(song, Arg.Any<DateTime>()).Returns(true);

        await _service.OnTrackEligibleForScrobblingAsync(song, 1, DateTime.UnixEpoch);

        await _scrobblerService.Received(1).SubmitListenAsync(song, DateTime.UnixEpoch);
        await _libraryWriter.Received(1).MarkListenAsSubmittedToListenBrainzAsync(1);
    }

    [Fact]
    public async Task OnTrackEligibleForScrobblingAsync_WhenRealtimeSubmitFails_DoesNotMarkAsSubmitted()
    {
        await InitializeServiceAsync(false, true);
        var song = CreateTestSong(TimeSpan.FromMinutes(3));
        await _service.OnTrackChangedAsync(song, 1);
        _scrobblerService.SubmitListenAsync(song, Arg.Any<DateTime>()).Returns(false);

        await _service.OnTrackEligibleForScrobblingAsync(song, 1, DateTime.UnixEpoch);

        await _libraryWriter.DidNotReceive().MarkListenAsSubmittedToListenBrainzAsync(Arg.Any<long>());
    }

    [Fact]
    public async Task OnTrackEligibleForScrobblingAsync_WhenRealtimeSubmitThrows_DoesNotMarkAsSubmitted()
    {
        await InitializeServiceAsync(false, true);
        var song = CreateTestSong(TimeSpan.FromMinutes(3));
        await _service.OnTrackChangedAsync(song, 1);
        _scrobblerService.SubmitListenAsync(song, Arg.Any<DateTime>()).ThrowsAsync(new Exception("Network error"));

        await _service.OnTrackEligibleForScrobblingAsync(song, 1, DateTime.UnixEpoch);

        await _libraryWriter.DidNotReceive().MarkListenAsSubmittedToListenBrainzAsync(Arg.Any<long>());
    }

}
