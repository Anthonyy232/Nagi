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
    private readonly Func<string, DiscordRpcClient> _createClient;
    private readonly object _clientLock = new();
    private DiscordRpcClient? _client;
    private TimeSpan _currentProgress;
    private Song? _currentSong;
    private Timestamps? _timestamps;
    private volatile bool _isPlaying;
    private volatile bool _isReady;

    private Timer? _debounceTimer;

    public DiscordPresenceService(IConfiguration configuration, ILogger<DiscordPresenceService> logger)
        : this(configuration, logger, appId => new DiscordRpcClient(appId, pipe: -1,
            logger: new DiscordLoggerAdapter(logger), autoEvents: true,
            client: new SandboxAwareDiscordPipeClient { Logger = new DiscordLoggerAdapter(logger) }))
    {
    }

    internal DiscordPresenceService(IConfiguration configuration, ILogger<DiscordPresenceService> logger,
        Func<string, DiscordRpcClient> createClient)
    {
        _logger = logger;
        _createClient = createClient;
        _discordAppId = configuration["Discord:AppId"];

        if (string.IsNullOrWhiteSpace(_discordAppId))
            _logger.LogWarning("Discord AppId is not configured. Discord Rich Presence will be disabled.");
    }

    public string Name => "Discord";

    public Task InitializeAsync()
    {
        if (string.IsNullOrWhiteSpace(_discordAppId)) return Task.CompletedTask;

        lock (_clientLock)
        {
            try
            {
                if (_client == null || _client.IsDisposed)
                {
                    _logger.LogDebug("Initializing new Discord RPC client.");
                    _isReady = false;
                    var client = _createClient(_discordAppId);
                    client.SkipIdenticalPresence = true;
                    _client = client;

                    client.OnError += OnRpcError;
                    client.OnReady += async (_, _) =>
                    {
                        _logger.LogInformation("Discord Rich Presence is Ready. Waiting for warm-up...");

                        // Let Discord finish initializing before sending presence, and ignore an old connection's callback.
                        await Task.Delay(2000).ConfigureAwait(false);
                        lock (_clientLock)
                        {
                            if (!ReferenceEquals(_client, client) || client.IsDisposed) return;
                            _isReady = true;
                            _logger.LogInformation("Discord warm-up complete. Syncing state.");
                            RequestUpdate();
                        }
                    };

                    client.OnConnectionFailed += (_, e) => _logger.LogWarning("Discord connection failed: {Pipe}", e.FailedPipe);
                    client.Initialize();
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
        }
        return Task.CompletedTask;
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
        RequestUpdate();
        return Task.CompletedTask;
    }

    private void RequestUpdate()
    {
        // Increase debounce to 500ms. Discord's rate limit is 1 update per 15s,
        // but the library specifically crashes if updates are sent within ~200ms of each other.
        lock (_clientLock)
        {
            if (_client == null) return;
            _debounceTimer ??= new Timer(_ => UpdatePresenceInternal(), null, Timeout.Infinite, Timeout.Infinite);
            _debounceTimer.Change(500, Timeout.Infinite);
        }
    }

    private void UpdatePresenceInternal()
    {
        try
        {
            var client = _client;
            if (client == null || !client.IsInitialized || !_isReady) return;

            // If no song is loaded or playback is stopped, show idling
            if (_currentSong == null)
            {
                client.SetPresence(new RichPresence { Details = "Browsing Music Library", State = "Idling" });
                return;
            }

            string stateText = _isPlaying
                ? (string.IsNullOrWhiteSpace(_currentSong.ArtistName) ? "Unknown Artist" : $"by {_currentSong.ArtistName}")
                : $"Paused | {FormatTimeSpan(_currentProgress)} / {FormatTimeSpan(_currentSong.Duration)}";

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
            client.SetPresence(presence);
        }
        catch (ObjectDisposedException)
        {
            // Expected if timer callback fires during/after DisposeAsync — safe to ignore.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error building presence update.");
        }
    }

    private static string FormatTimeSpan(TimeSpan ts)
    {
        if (ts.TotalHours >= 1)
            return $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}";
        return ts.ToString(@"m\:ss");
    }

    public Task OnTrackProgressAsync(TimeSpan progress, TimeSpan duration)
    {
        _currentProgress = progress;
        return Task.CompletedTask;
    }

    public Task OnTrackEligibleForScrobblingAsync(
        Song song,
        long listenHistoryId,
        DateTime listenStartedUtc) => Task.CompletedTask;

    public ValueTask DisposeAsync()
    {
        DiscordRpcClient? client;
        lock (_clientLock)
        {
            _isReady = false;
            _debounceTimer?.Dispose();
            _debounceTimer = null;
            client = _client;
            _client = null;
        }

        if (client != null)
        {
            _logger.LogDebug("Disposing Discord RPC client.");
            client.OnError -= OnRpcError;
            // Dispose outside the lock so RPC callbacks can finish while the client shuts down.
            client.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    private void OnRpcError(object sender, ErrorMessage e)
    {
        _logger.LogError("An error occurred in the Discord RPC client. Code: {ErrorCode}, Message: {ErrorMessage}", e.Code, e.Message);
    }
}
