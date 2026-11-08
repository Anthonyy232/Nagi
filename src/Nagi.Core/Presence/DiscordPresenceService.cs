using System.Collections.Concurrent;
using System.Diagnostics;
using DiscordRPC;
using DiscordRPC.Message;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Nagi.Core.Artwork;
using Nagi.Core.Library;
using Nagi.Core.Platform;
using Nagi.Core.Playback;
using Nagi.Core.Settings;

namespace Nagi.Core.Presence;

/// <summary>
///     Shows what is playing on the user's Discord profile while Discord Rich Presence is turned on.
/// </summary>
public class DiscordPresenceService : IDisposable
{
    private static readonly TimeSpan PositionCheckInterval = TimeSpan.FromSeconds(5);

    // Absorbs the small lag between reading the playback position and handling it, so normal
    // playback never spends an update from Discord's rate limit.
    private static readonly TimeSpan SyncTolerance = TimeSpan.FromSeconds(2);

    private readonly string? _discordAppId;
    private readonly ILogger<DiscordPresenceService> _logger;
    private readonly IMusicPlaybackService _playbackService;
    private readonly IAlbumCoverService _albumCoverService;
    private readonly ISettingsService _settingsService;
    private readonly Func<string, DiscordRpcClient> _createClient;
    private readonly object _clientLock = new();
    private DiscordRpcClient? _client;
    private TimeSpan _currentProgress;
    private Song? _currentSong;
    private Timestamps? _timestamps;

    // Discord can only show images by URL, so a local cover can't be sent; remember each album's online one.
    private readonly ConcurrentDictionary<Guid, string?> _coverUrls = new();
    private volatile SongCover? _cover;
    private volatile bool _isPlaying;
    private volatile bool _isReady;

    private Timer? _debounceTimer;
    private bool _isStarted;
    private Guid? _currentSongId;
    private long? _currentListenId;
    private long? _lastPositionCheck;

    public DiscordPresenceService(IConfiguration configuration, ILogger<DiscordPresenceService> logger,
        IMusicPlaybackService playbackService, IAlbumCoverService albumCoverService, ISettingsService settingsService)
        : this(configuration, logger, playbackService, albumCoverService, settingsService, appId => new DiscordRpcClient(appId,
            pipe: -1, logger: new DiscordLoggerAdapter(logger), autoEvents: true,
            client: new SandboxAwareDiscordPipeClient { Logger = new DiscordLoggerAdapter(logger) }))
    {
    }

    internal DiscordPresenceService(IConfiguration configuration, ILogger<DiscordPresenceService> logger,
        IMusicPlaybackService playbackService, IAlbumCoverService albumCoverService, ISettingsService settingsService,
        Func<string, DiscordRpcClient> createClient)
    {
        _logger = logger;
        _playbackService = playbackService;
        _albumCoverService = albumCoverService;
        _settingsService = settingsService;
        _createClient = createClient;
        _discordAppId = configuration["Discord:AppId"];

        if (string.IsNullOrWhiteSpace(_discordAppId))
            _logger.LogWarning("Discord AppId is not configured. Discord Rich Presence will be disabled.");
    }

    /// <summary>
    ///     Follows playback, and connects to Discord whenever Rich Presence is turned on.
    /// </summary>
    public void Start()
    {
        if (_isStarted) return;
        _isStarted = true;
        _playbackService.TrackChanged += OnTrackChanged;
        _playbackService.PlaybackStateChanged += OnPlaybackStateChanged;
        _playbackService.PositionChanged += OnPositionChanged;
        _settingsService.SettingChanged += OnSettingChanged;
        OnTrackChanged();
        OnPlaybackStateChanged();
        if (_settingsService.Get(CoreSettings.DiscordRichPresence)) Connect();
    }

    public void Stop()
    {
        if (!_isStarted) return;
        _isStarted = false;
        _playbackService.TrackChanged -= OnTrackChanged;
        _playbackService.PlaybackStateChanged -= OnPlaybackStateChanged;
        _playbackService.PositionChanged -= OnPositionChanged;
        _settingsService.SettingChanged -= OnSettingChanged;
        Disconnect();
    }

    public void Dispose() => Stop();

    private void OnSettingChanged(Setting setting)
    {
        if (setting != CoreSettings.DiscordRichPresence) return;
        if (_settingsService.Get(CoreSettings.DiscordRichPresence)) Connect();
        else Disconnect();
    }

    private void Connect()
    {
        if (string.IsNullOrWhiteSpace(_discordAppId)) return;

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

                        // A track that started before Discord connected skipped its cover lookup.
                        if (_currentSong is { } song) _ = ShowAlbumCoverAsync(song);
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
    }

    private void OnTrackChanged()
    {
        var song = _playbackService.CurrentTrack;
        var listenId = _playbackService.CurrentListenHistoryId;
        // A song played again gets a new listen, which restarts its time on the profile.
        if (song?.Id == _currentSongId && listenId == _currentListenId) return;
        _currentSongId = song?.Id;
        _currentListenId = listenId;
        _lastPositionCheck = null;

        if (song is null)
        {
            // A stopped player still has a track loaded, so it shows as paused rather than clearing the presence,
            // which is unstable in the library.
            _isPlaying = false;
            _timestamps = null;
            RequestUpdate();
            return;
        }

        _currentSong = song;
        _currentProgress = TimeSpan.Zero;
        _timestamps = BuildTimestamps(TimeSpan.Zero);
        RequestUpdate();

        // Without a Discord connection there is nothing to show the cover on, so skip the network lookup.
        if (_isReady) _ = ShowAlbumCoverAsync(song);
    }

    private async Task ShowAlbumCoverAsync(Song song)
    {
        try
        {
            var url = await FindCoverUrlAsync(song).ConfigureAwait(false);
            if (url is null || !ReferenceEquals(_currentSong, song)) return;
            _cover = new SongCover(song, url);
            RequestUpdate();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to find an album cover for Discord.");
        }
    }

    internal async Task<string?> FindCoverUrlAsync(Song song)
    {
        if (song.Album is not { } album) return null;
        if (_coverUrls.TryGetValue(album.Id, out var cachedUrl)) return cachedUrl;

        var result = await _albumCoverService.FindCoverUrlAsync(
            new AlbumCoverQuery(album.Title, album.PrimaryArtistName, song.MusicBrainzReleaseId, song.MusicBrainzReleaseGroupId))
            .ConfigureAwait(false);
        if (result is null) return null;

        // Discord rejects image URLs longer than 256 characters.
        var url = result.Data is { Length: <= 256 } data ? data : null;
        if (result.IsConclusive) _coverUrls[album.Id] = url;
        return url;
    }

    private void OnPlaybackStateChanged()
    {
        _isPlaying = _playbackService.IsPlaying;
        _timestamps = _isPlaying ? BuildTimestamps(_currentProgress) : null;
        RequestUpdate();
    }

    private void RequestUpdate()
    {
        // Discord's rate limit is 1 update per 15s, but the library specifically crashes if updates are sent within ~200ms of each other.
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

            if (_currentSong == null)
            {
                client.SetPresence(new RichPresence
                {
                    Type = ActivityType.Listening,
                    Details = Resources.Strings.Presence_BrowsingLibrary,
                    State = Resources.Strings.Presence_Idling
                });
                return;
            }

            string stateText = _isPlaying
                ? string.IsNullOrWhiteSpace(_currentSong.ArtistName)
                    ? Artist.UnknownArtistName
                    : string.Format(Resources.Strings.Presence_ByArtist, _currentSong.ArtistName)
                : string.Format(Resources.Strings.Presence_PausedProgress,
                    DurationFormatter.Format(_currentProgress), DurationFormatter.Format(_currentSong.Duration));

            var presence = new RichPresence
            {
                Type = ActivityType.Listening,
                Details = _currentSong.Title.Truncate(128),
                State = stateText.Truncate(128),
                StatusDisplay = StatusDisplayType.Details,
                Timestamps = _timestamps,
                Assets = new Assets
                {
                    // A lookup for the previous track can finish after the switch; never show its cover.
                    LargeImageKey = _cover is { } cover && ReferenceEquals(cover.Song, _currentSong) ? cover.Url : "logo",
                    LargeImageText = (_currentSong.Album?.Title ?? Resources.Strings.Presence_AppName).Truncate(128),
                    SmallImageKey = _isPlaying ? "play_icon" : "pause_icon",
                    SmallImageText = _isPlaying ? Resources.Strings.Presence_Playing : Resources.Strings.Presence_Paused
                }
            };

            _logger.LogDebug("Sending Discord Presence Update: {Details}", presence.Details);
            client.SetPresence(presence);
        }
        catch (ObjectDisposedException)
        {
            // Expected if the timer callback fires while disconnecting — safe to ignore.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error building presence update.");
        }
    }

    private void OnPositionChanged()
    {
        if (_currentSongId is null || _playbackService.Duration <= TimeSpan.Zero) return;

        // Position changes many times a second, and drift only matters past the sync tolerance.
        var now = Stopwatch.GetTimestamp();
        if (_lastPositionCheck is { } lastCheck && Stopwatch.GetElapsedTime(lastCheck, now) < PositionCheckInterval) return;
        _lastPositionCheck = now;

        var progress = _playbackService.CurrentPosition;
        var previousProgress = _currentProgress;
        _currentProgress = progress;

        if (_isPlaying)
        {
            // After a seek or a speed change the timestamps no longer match playback, so Discord's
            // clock would stay wrong until the next pause or track change.
            if (IsOutOfSync(_timestamps, progress, _playbackService.PlaybackRate, DateTime.UtcNow))
            {
                _timestamps = BuildTimestamps(progress);
                RequestUpdate();
            }
        }
        else if ((progress - previousProgress).Duration() > SyncTolerance)
        {
            // Seeking while paused changes the position shown in the paused text.
            RequestUpdate();
        }
    }

    // Discord's clock runs in real time, so scale by the playback speed to keep it in step with playback.
    private Timestamps BuildTimestamps(TimeSpan progress)
    {
        var rate = _playbackService.PlaybackRate;
        var start = DateTime.UtcNow - progress / rate;
        var duration = _currentSong?.Duration ?? TimeSpan.Zero;

        // Discord shows elapsed and remaining time together when both ends are known.
        return duration > TimeSpan.Zero
            ? new Timestamps { Start = start, End = start + duration / rate }
            : new Timestamps { Start = start };
    }

    internal static bool IsOutOfSync(Timestamps? timestamps, TimeSpan progress, double playbackRate, DateTime utcNow) =>
        timestamps?.Start is { } start && ((utcNow - start) * playbackRate - progress).Duration() > SyncTolerance;

    private void Disconnect()
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
    }

    private void OnRpcError(object sender, ErrorMessage e)
    {
        _logger.LogError("An error occurred in the Discord RPC client. Code: {ErrorCode}, Message: {ErrorMessage}", e.Code, e.Message);
    }

    private sealed record SongCover(Song Song, string Url);
}
