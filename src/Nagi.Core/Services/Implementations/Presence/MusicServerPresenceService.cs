using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;

namespace Nagi.Core.Services.Implementations.Presence;

/// <summary>
///     Counts plays of server songs on their server as soon as they reach the scrobble threshold, so its play counts
///     match other apps. Plays that can't be reported stay queued for the next attempt.
/// </summary>
public class MusicServerPresenceService : IPresenceService
{
    private readonly IMusicServerService _musicServers;

    public MusicServerPresenceService(IMusicServerService musicServers)
    {
        _musicServers = musicServers;
    }

    public string Name => "Music servers";

    public Task InitializeAsync() => Task.CompletedTask;

    public Task OnTrackChangedAsync(Song song, long listenHistoryId) => Task.CompletedTask;

    public Task OnPlaybackStateChangedAsync(bool isPlaying) => Task.CompletedTask;

    public Task OnPlaybackStoppedAsync() => Task.CompletedTask;

    public Task OnTrackProgressAsync(TimeSpan progress, TimeSpan duration) => Task.CompletedTask;

    // The play is already queued, so reporting the server's queue sends it along with any that failed earlier.
    public Task OnTrackEligibleForScrobblingAsync(Song song, long listenHistoryId, DateTime listenStartedUtc) =>
        song.IsRemote ? _musicServers.ReportPlaysAsync(song.FolderId) : Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
