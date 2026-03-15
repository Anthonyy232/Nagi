using DiscordRPC;
using DiscordRPC.Message;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Nagi.Core.Helpers;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;

namespace Nagi.Core.Services.Implementations.Presence;

public class DiscordPresenceService : IPresenceService, IAsyncDisposable
{
    private readonly string? _discordAppId;
    private readonly ILogger<DiscordPresenceService> _logger;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private DiscordRpcClient? _client;
    private TimeSpan _currentProgress;
    private Song? _currentSong;
    private Timestamps? _timestamps;
    private volatile bool _isPlaying;
    private volatile bool _isReady;

    private Timer? _debounceTimer;
    private readonly object _timerLock = new();

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
            if (_client == null || _client.IsDisposed)
            {
                _logger.LogDebug("Initializing new Discord RPC client.");
                
                var pipeClient = new SandboxAwareDiscordPipeClient { Logger = new DiscordLoggerAdapter(_logger) };
                
                // Set SkipIdenticalPresence to true to prevent redundant updates that trigger library bugs
                _client = new DiscordRpcClient(_discordAppId, pipe: -1, logger: new DiscordLoggerAdapter(_logger), autoEvents: true, client: pipeClient)
                {
                    SkipIdenticalPresence = true 
                };
                
                _client.OnError += OnRpcError;
                _client.OnReady += async (_, _) =>
                {
                    _logger.LogInformation("Discord Rich Presence is Ready. Waiting for warm-up...");

                    // Give the library 2 seconds to initialize its internal state before we push data.
                    // Guard against disposal racing with this delay.
                    await Task.Delay(2000).ConfigureAwait(false);

                    if (_client is not { IsDisposed: false }) return;
                    _isReady = true;
                    _logger.LogInformation("Discord warm-up complete. Syncing state.");
                    RequestUpdate();
                };

                _client.OnConnectionFailed += (_, e) => _logger.LogWarning("Discord connection failed: {Pipe}", e.FailedPipe);
                
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
        finally { _initLock.Release(); }
    }

    public Task OnTrackChangedAsync(Song song, long listenHistoryId)
    {
        _currentSong = song;
        _currentProgress = TimeSpan.Zero;
        _timestamps = new Timestamps { Start = DateTime.UtcNow };
        RequestUpdate();
        return Task.CompletedTask;
    }

    public Task OnPlaybackStateChangedAsync(bool isPlaying)
    {
        _isPlaying = isPlaying;
        
        if (isPlaying)
            _timestamps = new Timestamps { Start = DateTime.UtcNow - _currentProgress };
        else
            _timestamps = null;

        RequestUpdate();
        return Task.CompletedTask;
    }

    public Task OnPlaybackStoppedAsync()
    {
        // Do NOT clear _currentSong — a stopped player still has a track loaded.
        // We show the paused-track state rather than calling ClearPresence (which is unstable in the library).
        _isPlaying = false;
        _timestamps = null;
        lock (_timerLock) _debounceTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        RequestUpdate();
        return Task.CompletedTask;
    }

    private void RequestUpdate()
    {
        // DiscordRPC natively queues presence updates if the pipe isn't ready, so we only need to check if _client exists
        if (_client == null) return;

        // Increase debounce to 500ms. Discord's rate limit is 1 update per 15s, 
        // but the library specifically crashes if updates are sent within ~200ms of each other.
        lock (_timerLock)
        {
            _debounceTimer ??= new Timer(_ => UpdatePresenceInternal(), null, Timeout.Infinite, Timeout.Infinite);
            _debounceTimer.Change(500, Timeout.Infinite);
        }
    }

    private void UpdatePresenceInternal()
    {
        // Ensure we are initialized AND the Discord handshake has finished
        if (_client == null || !_client.IsInitialized || !_isReady) return;

        // If no song is loaded or playback is stopped, show idling
        if (_currentSong == null)
        {
            _client.SetPresence(new RichPresence { Details = "Browsing Music Library", State = "Idling" });
            return;
        }

        try
        {
            string stateText = _isPlaying 
                ? (string.IsNullOrWhiteSpace(_currentSong.ArtistName) ? "Unknown Artist" : $"by {_currentSong.ArtistName}")
                : $"Paused | {_currentProgress:mm\\:ss} / {_currentSong.Duration:mm\\:ss}";

            var presence = new RichPresence
            {
                Details = (_currentSong.Title ?? "Unknown").Truncate(128),
                State = stateText.Truncate(128),
                StatusDisplay = StatusDisplayType.Details,
                Timestamps = _timestamps,
                Assets = new Assets
                {
                    LargeImageKey = "logo",
                    LargeImageText = (_currentSong.Album?.Title ?? "Nagi Music Player").Truncate(128),
                    SmallImageKey = _isPlaying ? "play_icon" : "pause_icon",
                    SmallImageText = (_isPlaying ? "Playing" : "Paused")
                }
            };

            _logger.LogDebug("Sending Discord Presence Update: {Details}", presence.Details);
            _client.SetPresence(presence);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error building presence update.");
        }
    }

    public Task OnTrackProgressAsync(TimeSpan progress, TimeSpan duration)
    {
        _currentProgress = progress;
        return Task.CompletedTask;
    }

    public Task OnTrackEligibleForScrobblingAsync(Song song, long listenHistoryId) => Task.CompletedTask;

    public ValueTask DisposeAsync()
    {
        _isReady = false;
        _initLock.Dispose();
        _debounceTimer?.Dispose();

        if (_client != null)
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
        _logger.LogError("An error occurred in the Discord RPC client. Code: {ErrorCode}, Message: {ErrorMessage}", e.Code, e.Message);
    }
}