using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;
using Nagi.Presentation.Helpers;
using Nagi.Presentation.Resources;
using Nagi.Presentation.Services.Abstractions;

namespace Nagi.Presentation.Services;

/// <summary>
///     Tells the user why a song from a music server won't play.
/// </summary>
public sealed class MusicServerPrompts : IDisposable
{
    private readonly IMusicServerService _musicServers;
    private readonly IMusicPlaybackService _playback;
    private readonly IUIService _ui;
    private readonly IDispatcherService _dispatcher;
    private readonly ILogger<MusicServerPrompts> _logger;
    // One message at a time: playing a song again while its message is open fails again.
    private bool _isShowingMessage;

    public MusicServerPrompts(IMusicServerService musicServers, IMusicPlaybackService playback, IUIService ui,
        IDispatcherService dispatcher, ILogger<MusicServerPrompts> logger)
    {
        _musicServers = musicServers;
        _playback = playback;
        _ui = ui;
        _dispatcher = dispatcher;
        _logger = logger;
    }

    public void Start()
    {
        _playback.ServerSongFailed += OnServerSongFailed;
    }

    public void Dispose()
    {
        _playback.ServerSongFailed -= OnServerSongFailed;
    }

    private void OnServerSongFailed(Song song) => _dispatcher.TryEnqueue(() => _ = ExplainServerSongFailureAsync(song));

    // Asks the server what went wrong, so the message can say what to do about it.
    private async Task ExplainServerSongFailureAsync(Song song)
    {
        if (_isShowingMessage) return;
        _isShowingMessage = true;
        try
        {
            MusicServerError? error = null;
            try
            {
                await _musicServers.CheckSongAsync(song);
            }
            catch (MusicServerException ex)
            {
                error = ex.Error;
                _logger.LogWarning(ex, "Server song {SongId} couldn't be played.", song.Id);
            }
            // Not knowing why still gets the plain message below.
            catch (Exception ex)
            {
                _logger.LogError(ex, "Server song {SongId} couldn't be played.", song.Id);
            }

            // A server removed meanwhile took its songs with it, so there's nothing left to explain.
            if (await _musicServers.GetServerAsync(song.FolderId) is not { } server) return;
            var name = server.Folder.Name;
            await MusicServerErrors.ShowAsync(_ui, song.FolderId, name, error, error == MusicServerError.NotFound
                ? string.Format(Strings.Player_ServerSongGone_Format, song.Title, name)
                : string.Format(Strings.Player_ServerSongFailed_Format, song.Title, name));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to explain why server song {SongId} couldn't be played.", song.Id);
        }
        finally
        {
            _isShowingMessage = false;
        }
    }
}
