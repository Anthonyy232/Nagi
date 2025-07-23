using CommunityToolkit.Common;
using DiscordRPC;
using Microsoft.Extensions.Configuration;
using Nagi.Models;
using Nagi.Services.Abstractions;
using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace Nagi.Services.Presence;

/// <summary>
/// Provides Discord Rich Presence integration.
/// </summary>
public class DiscordPresenceService : IPresenceService, IAsyncDisposable {
    private DiscordRpcClient? _client;
    private Song? _currentSong;
    private Timestamps? _timestamps;
    private TimeSpan _currentProgress;
    private readonly string? _discordAppId;

    public string Name => "Discord";

    public DiscordPresenceService(IConfiguration configuration) {
        _discordAppId = configuration["Discord:AppId"];

        if (string.IsNullOrEmpty(_discordAppId)) {
            Debug.WriteLine("[DiscordPresenceService] Discord AppId is not configured. Presence will be disabled.");
        }
    }

    public Task InitializeAsync() {
        if (string.IsNullOrEmpty(_discordAppId)) {
            return Task.CompletedTask;
        }

        // Initialize or re-initialize the client if it's null or disposed.
        if (_client == null || _client.IsDisposed) {
            _client = new DiscordRpcClient(_discordAppId);
            _client.OnError += (s, e) => Debug.WriteLine($"[DiscordPresenceService] RPC Error: {e.Message}");
            _client.Initialize();
        }
        else if (!_client.IsInitialized) {
            _client.Initialize();
        }

        return Task.CompletedTask;
    }

    public Task OnTrackChangedAsync(Song song, long listenHistoryId) {
        if (_client is not { IsInitialized: true }) return Task.CompletedTask;

        _currentSong = song;
        _currentProgress = TimeSpan.Zero;

        // Set the start time to begin the "time elapsed" counter.
        _timestamps = new Timestamps() {
            Start = DateTime.UtcNow
        };

        UpdatePresence();
        return Task.CompletedTask;
    }

    public Task OnPlaybackStateChangedAsync(bool isPlaying) {
        if (_client is not { IsInitialized: true } || _currentSong is null) return Task.CompletedTask;

        if (isPlaying) {
            // When resuming, set the start time to a past moment to make the "elapsed"
            // timer show the correct current progress.
            _timestamps = new Timestamps() {
                Start = DateTime.UtcNow - _currentProgress
            };
        }
        else {
            // When paused, clear timestamps to stop the timer on Discord.
            _timestamps = null;
        }

        UpdatePresence(isPlaying);
        return Task.CompletedTask;
    }

    public Task OnPlaybackStoppedAsync() {
        _currentSong = null;
        _currentProgress = TimeSpan.Zero;

        if (_client is { IsInitialized: true }) {
            _client.ClearPresence();
        }
        return Task.CompletedTask;
    }

    public Task OnTrackProgressAsync(TimeSpan progress, TimeSpan duration) {
        // Continuously update the current progress for accurate pause/resume behavior.
        _currentProgress = progress;
        return Task.CompletedTask;
    }

    private void UpdatePresence(bool isPlaying = true) {
        if (_client is not { IsInitialized: true } || _currentSong is null) {
            return;
        }

        string state;
        if (isPlaying) {
            state = $"by {_currentSong.Artist?.Name ?? "Unknown Artist"}".Truncate(128);
        }
        else {
            // When paused, display the progress directly in the state text.
            var current = _currentProgress.ToString(@"mm\:ss");
            var total = _currentSong.Duration.ToString(@"mm\:ss");
            state = $"Paused | {current} / {total}".Truncate(128);
        }

        var presence = new RichPresence {
            Details = _currentSong.Title.Truncate(128),
            State = state,
            Timestamps = _timestamps,
            Assets = new Assets {
                LargeImageKey = "logo",
                LargeImageText = _currentSong.Album?.Title ?? string.Empty,
                SmallImageKey = isPlaying ? "play_icon" : "pause_icon",
                SmallImageText = isPlaying ? "Playing" : "Paused"
            }
        };

        _client.SetPresence(presence);
    }

    public ValueTask DisposeAsync() {
        if (_client is { IsDisposed: false }) {
            _client.Dispose();
        }
        _client = null;
        return ValueTask.CompletedTask;
    }
}