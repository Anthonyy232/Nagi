using Nagi.Core.Services.Abstractions;
using Nagi.Core.Settings;

namespace Nagi.Core.Scrobbling;

public sealed class ScrobblingAccounts : IScrobblingAccounts
{
    private readonly ISettingsService _settingsService;

    public ScrobblingAccounts(ISettingsService settingsService)
    {
        _settingsService = settingsService;
    }

    public async Task<bool> ConnectLastFmAsync(string username, string sessionKey)
    {
        if (!await _settingsService.SaveLastFmCredentialsAsync(username, sessionKey).ConfigureAwait(false)) return false;
        _settingsService.Set(CoreSettings.LastFmScrobbling, true);
        _settingsService.Set(CoreSettings.LastFmNowPlaying, true);
        return true;
    }

    public async Task DisconnectLastFmAsync()
    {
        await _settingsService.ClearLastFmCredentialsAsync().ConfigureAwait(false);
        _settingsService.Set(CoreSettings.LastFmScrobbling, false);
        _settingsService.Set(CoreSettings.LastFmNowPlaying, false);
        _settingsService.Set(CoreSettings.LastFmAuthToken, null);
    }

    public async Task<bool> ConnectListenBrainzAsync(string token)
    {
        if (!await _settingsService.SaveListenBrainzUserTokenAsync(token).ConfigureAwait(false)) return false;
        // Sending every listen ever made would flood the account with history it may already have.
        if (_settingsService.Get(CoreSettings.ListenBrainzEnabledSinceUtc) is null)
            _settingsService.Set(CoreSettings.ListenBrainzEnabledSinceUtc, DateTime.UtcNow);
        return true;
    }

    public async Task DisconnectListenBrainzAsync()
    {
        await _settingsService.ClearListenBrainzUserTokenAsync().ConfigureAwait(false);
        _settingsService.Set(CoreSettings.ListenBrainzScrobbling, false);
        _settingsService.Set(CoreSettings.ListenBrainzNowPlaying, false);
        _settingsService.Set(CoreSettings.ListenBrainzEnabledSinceUtc, null);
    }
}
