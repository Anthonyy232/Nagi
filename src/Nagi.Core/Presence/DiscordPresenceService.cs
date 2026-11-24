using System.Collections.Concurrent;
using System.Diagnostics;
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
    private readonly Func<string, DiscordIpcClient> _createClient;
    private readonly Lock _clientLock = new();
    private DiscordIpcClient? _client;
    private TimeSpan _currentProgress;
    private Song? _currentSong;
    private DiscordTimestamps? _timestamps;

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
        : this(configuration, logger, playbackService, albumCoverService, settingsService,
            appId => new DiscordIpcClient(appId, logger))
    {
    }

    internal DiscordPresenceService(IConfiguration configuration, ILogger<DiscordPresenceService> logger,
        IMusicPlaybackService playbackService, IAlbumCoverService albumCoverService, ISettingsService settingsService,
        Func<string, DiscordIpcClient> createClient)
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
            if (_client != null) return;
            _logger.LogDebug("Starting the Discord client.");
            _isReady = false;
            var client = _createClient(_discordAppId);
            _client = client;
            client.Ready += () => _ = OnReadyAsync(client);
            client.Start();
        }
    }

    private async Task OnReadyAsync(DiscordIpcClient client)
    {
        _logger.LogInformation("Discord Rich Presence is Ready. Waiting for warm-up...");

        // Let Discord finish initializing before sending presence, and ignore an old connection's callback.
        await Task.Delay(2000).ConfigureAwait(false);
        lock (_clientLock)
        {
            if (!ReferenceEquals(_client, client)) return;
            _isReady = true;
            _logger.LogInformation("Discord warm-up complete. Syncing state.");
            RequestUpdate();
        }

        // A track that started before Discord connected skipped its cover lookup.
        if (_currentSong is { } song) _ = ShowAlbumCoverAsync(song);
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
        // Coalesces bursts of changes, such as skipping through tracks, into one update.
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
            if (client == null || !_isReady) return;

            if (_currentSong == null)
            {
                _ = client.SetActivityAsync(new DiscordActivity
                {
                    Details = Resources.Strings.Presence_BrowsingLibrary,
                    State = Resources.Strings.Presence_Idling
                });
                return;
            }

            var artist = _currentSong.ArtistName;
            var progress = DurationFormatter.Format(_currentProgress);
            var duration = DurationFormatter.Format(_currentSong.Duration);
            var stateText = (_isPlaying, string.IsNullOrWhiteSpace(artist)) switch
            {
                (true, false) => string.Format(Resources.Strings.Presence_ByArtist, artist),
                (true, true) => Artist.UnknownArtistName,
                (false, false) => string.Format(Resources.Strings.Presence_PausedByArtist, progress, duration, artist),
                (false, true) => string.Format(Resources.Strings.Presence_PausedProgress, progress, duration)
            };

            var activity = new DiscordActivity
            {
                Details = _currentSong.Title.Truncate(128),
                State = stateText.Truncate(128),
                StatusDisplayType = DiscordStatusDisplay.Details,
                Timestamps = _timestamps,
                Assets = new DiscordAssets(
                    // A lookup for the previous track can finish after the switch; never show its cover.
                    _cover is { } cover && ReferenceEquals(cover.Song, _currentSong) ? cover.Url : "logo",
                    (_currentSong.Album?.Title ?? Resources.Strings.Presence_AppName).Truncate(128),
                    _isPlaying ? "play_icon" : "pause_icon",
                    _isPlaying ? Resources.Strings.Presence_Playing : Resources.Strings.Presence_Paused)
            };

            _logger.LogDebug("Sending Discord Presence Update: {Details}", activity.Details);
            _ = client.SetActivityAsync(activity);
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
            if (IsOutOfSync(_timestamps, progress, _playbackService.PlaybackRate, DateTimeOffset.UtcNow))
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
    private DiscordTimestamps BuildTimestamps(TimeSpan progress)
    {
        var rate = _playbackService.PlaybackRate;
        var start = DateTimeOffset.UtcNow - progress / rate;
        var duration = _currentSong?.Duration ?? TimeSpan.Zero;

        // Discord shows elapsed and remaining time together when both ends are known.
        return new DiscordTimestamps(start.ToUnixTimeMilliseconds(),
            duration > TimeSpan.Zero ? (start + duration / rate).ToUnixTimeMilliseconds() : null);
    }

    internal static bool IsOutOfSync(DiscordTimestamps? timestamps, TimeSpan progress, double playbackRate,
        DateTimeOffset utcNow) =>
        timestamps is { } t &&
        ((utcNow - DateTimeOffset.FromUnixTimeMilliseconds(t.Start)) * playbackRate - progress).Duration() > SyncTolerance;

    private void Disconnect()
    {
        DiscordIpcClient? client;
        lock (_clientLock)
        {
            _isReady = false;
            _debounceTimer?.Dispose();
            _debounceTimer = null;
            client = _client;
            _client = null;
        }

        if (client == null) return;
        _logger.LogDebug("Stopping the Discord client.");
        client.Dispose();
    }

    private sealed record SongCover(Song Song, string Url);
}
