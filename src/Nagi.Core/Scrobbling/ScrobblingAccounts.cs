using Nagi.Core.Settings;

namespace Nagi.Core.Scrobbling;

public sealed class ScrobblingAccounts : IScrobblingAccounts
{
    private readonly ISettingsService _settingsService;
    private readonly ILastFmAuthService _lastFmAuth;
    // Bumped when a Last.fm sign-in starts or ends, so a reply that arrives after the user cancelled is ignored.
    private int _lastFmSignInVersion;

    public ScrobblingAccounts(ISettingsService settingsService, ILastFmAuthService lastFmAuth)
    {
        _settingsService = settingsService;
        _lastFmAuth = lastFmAuth;
    }

    public bool IsLastFmSignInPending => !string.IsNullOrEmpty(_settingsService.Get(CoreSettings.LastFmAuthToken));

    public async Task<Uri?> BeginLastFmSignInAsync()
    {
        var version = Interlocked.Increment(ref _lastFmSignInVersion);
        var authentication = await _lastFmAuth.GetAuthenticationTokenAsync().ConfigureAwait(false);
        if (authentication is not { } started || version != Volatile.Read(ref _lastFmSignInVersion)) return null;

        _settingsService.Set(CoreSettings.LastFmAuthToken, started.Token);
        return new Uri(started.AuthUrl);
    }

    public async Task<LastFmSignInResult> FinishLastFmSignInAsync()
    {
        var version = Volatile.Read(ref _lastFmSignInVersion);
        var token = _settingsService.Get(CoreSettings.LastFmAuthToken);
        if (string.IsNullOrEmpty(token)) return LastFmSignInResult.NotStarted;

        try
        {
            var session = await _lastFmAuth.GetSessionAsync(token).ConfigureAwait(false);
            if (version != Volatile.Read(ref _lastFmSignInVersion)) return LastFmSignInResult.NotStarted;
            if (session is not { } signedIn) return LastFmSignInResult.Failed;
            return await ConnectLastFmAsync(signedIn.Username, signedIn.SessionKey).ConfigureAwait(false)
                ? LastFmSignInResult.Connected
                : LastFmSignInResult.NotSaved;
        }
        finally
        {
            if (version == Volatile.Read(ref _lastFmSignInVersion)) CancelLastFmSignIn();
        }
    }

    public void CancelLastFmSignIn()
    {
        Interlocked.Increment(ref _lastFmSignInVersion);
        _settingsService.Set(CoreSettings.LastFmAuthToken, null);
    }

    private async Task<bool> ConnectLastFmAsync(string username, string sessionKey)
    {
        if (!await _settingsService.SaveLastFmCredentialsAsync(username, sessionKey).ConfigureAwait(false)) return false;
        _settingsService.Set(CoreSettings.LastFmScrobbling, true);
        _settingsService.Set(CoreSettings.LastFmNowPlaying, true);
        return true;
    }

    public async Task DisconnectLastFmAsync()
    {
        CancelLastFmSignIn();
        await _settingsService.ClearLastFmCredentialsAsync().ConfigureAwait(false);
        _settingsService.Set(CoreSettings.LastFmScrobbling, false);
        _settingsService.Set(CoreSettings.LastFmNowPlaying, false);
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
