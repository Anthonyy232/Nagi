using DiscordRPC;
using DiscordRPC.Message;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Nagi.Core.Helpers;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;

namespace Nagi.Core.Services.Implementations.Presence;

/// <summary>
///     Provides Discord Rich Presence integration, showing the current track and playback status.
/// </summary>
public class DiscordPresenceService : IPresenceService, IAsyncDisposable
{
    private readonly string? _discordAppId;
    private readonly ILogger<DiscordPresenceService> _logger;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private DiscordRpcClient? _client;
    private TimeSpan _currentProgress;
    private Song? _currentSong;
    private Timestamps? _timestamps;

    public DiscordPresenceService(IConfiguration configuration, ILogger<DiscordPresenceService> logger)
    {
        _logger = logger;
        _discordAppId = configuration["Discord:AppId"];

        if (string.IsNullOrEmpty(_discordAppId))
            _logger.LogWarning("Discord AppId is not configured. Discord Rich Presence will be disabled.");
    }

    public string Name => "Discord";

    public async Task InitializeAsync()
    {
        if (string.IsNullOrEmpty(_discordAppId)) return;

        await _initLock.WaitAsync().ConfigureAwait(false);
        try
        {
            // Initialize the client if it hasn't been or if it was previously disposed.
            if (_client == null || _client.IsDisposed)
            {
                _logger.LogDebug("Initializing new Discord RPC client.");
                _client = new DiscordRpcClient(_discordAppId);
                _client.OnError += OnRpcError;
                _client.Initialize();
            }
            else if (!_client.IsInitialized)
            {
                _logger.LogDebug("Re-initializing existing Discord RPC client.");
                _client.Initialize();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize Discord RPC client.");
        }
        finally
        {
            _initLock.Release();
        }
    }

    public Task OnTrackChangedAsync(Song song, long listenHistoryId)
    {
        if (_client is not { IsInitialized: true }) return Task.CompletedTask;

        _logger.LogDebug("Updating Discord presence for new track: {TrackTitle}", song.Title);
        _currentSong = song;
        _currentProgress = TimeSpan.Zero;

        // Set the start time to begin the "time elapsed" counter on Discord.
        _timestamps = new Timestamps { Start = DateTime.UtcNow };

        UpdatePresence();
        return Task.CompletedTask;
    }

    public Task OnPlaybackStateChangedAsync(bool isPlaying)
    {
        if (_client is not { IsInitialized: true } || _currentSong is null) return Task.CompletedTask;

        _logger.LogDebug("Updating Discord presence for playback state change. IsPlaying: {IsPlaying}",
            isPlaying);

        if (isPlaying)
            // When resuming, set the start time to a past moment. This makes the "elapsed"
            // timer on Discord display the correct current progress of the track.
            _timestamps = new Timestamps { Start = DateTime.UtcNow - _currentProgress };
        else
            // When paused, clear timestamps to stop the timer on Discord.
            _timestamps = null;

        UpdatePresence(isPlaying);
        return Task.CompletedTask;
    }

    public Task OnPlaybackStoppedAsync()
    {
        _logger.LogDebug("Clearing Discord presence due to playback stop.");
        _currentSong = null;
        _currentProgress = TimeSpan.Zero;
        _timestamps = null;

        if (_client is { IsInitialized: true }) _client.ClearPresence();
        return Task.CompletedTask;
    }

    public Task OnTrackProgressAsync(TimeSpan progress, TimeSpan duration)
    {
        // Continuously track progress for accurate pause/resume timestamp calculations.
        _currentProgress = progress;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _initLock.Dispose();

        if (_client is not null)
        {
            _logger.LogDebug("Disposing Discord RPC client.");
            _client.OnError -= OnRpcError;
            if (!_client.IsDisposed) _client.Dispose();
            _client = null;
        }

        return ValueTask.CompletedTask;
    }

    private void OnRpcError(object sender, ErrorMessage e)
    {
        _logger.LogError("An error occurred in the Discord RPC client. Code: {ErrorCode}, Message: {ErrorMessage}",
            e.Code, e.Message);
    }

    private void UpdatePresence(bool isPlaying = true)
    {
        if (_client is not { IsInitialized: true } || _currentSong is null) return;

        string state;
        if (isPlaying)
        {
            state = $"by {_currentSong.ArtistName}".Truncate(128);
        }
        else
        {
            // When paused, display the progress directly in the state text.
            var current = _currentProgress.ToString(@"mm\:ss");
            var total = _currentSong.Duration.ToString(@"mm\:ss");
            state = $"Paused | {current} / {total}".Truncate(128);
        }

        var presence = new RichPresence
        {
            Details = _currentSong.Title.Truncate(128),
            State = state,
            StatusDisplay = StatusDisplayType.Details,
            Timestamps = _timestamps,
            Assets = new Assets
            {
                LargeImageKey = "logo",
                LargeImageText = _currentSong.Album?.Title ?? string.Empty,
                SmallImageKey = isPlaying ? "play_icon" : "pause_icon",
                SmallImageText = isPlaying ? "Playing" : "Paused"
            }
        };

        _client.SetPresence(presence);
    }
}