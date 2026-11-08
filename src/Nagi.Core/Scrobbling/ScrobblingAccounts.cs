using Microsoft.Extensions.Logging;
using Nagi.Core.Platform;
using Nagi.Core.Settings;

namespace Nagi.Core.Scrobbling;

public sealed class ScrobblingAccounts : IScrobblingAccounts
{
    private const string LastFmCredentialResource = "Nagi/LastFm";
    private const string ListenBrainzTokenResource = "Nagi/ListenBrainz";
    private const string ListenBrainzTokenUserName = "token";

    private readonly ISettingsService _settingsService;
    private readonly ICredentialLockerService _credentials;
    private readonly ILastFmAuthService _lastFmAuth;
    private readonly ILogger<ScrobblingAccounts> _logger;
    // Bumped when a Last.fm sign-in starts or ends, so a reply that arrives after the user cancelled is ignored.
    private int _lastFmSignInVersion;

    public ScrobblingAccounts(ISettingsService settingsService, ICredentialLockerService credentials,
        ILastFmAuthService lastFmAuth, ILogger<ScrobblingAccounts> logger)
    {
        _settingsService = settingsService;
        _credentials = credentials;
        _lastFmAuth = lastFmAuth;
        _logger = logger;
    }

    public event Action<ScrobblingAccount>? CredentialsChanged;

    public bool IsLastFmSignInPending => !string.IsNullOrEmpty(_settingsService.Get(CoreSettings.LastFmAuthToken));

    public Task<(string? Username, string? SessionKey)?> GetLastFmCredentialsAsync() =>
        Task.FromResult(_credentials.RetrieveCredential(LastFmCredentialResource));

    public Task<string?> GetListenBrainzUserTokenAsync() =>
        Task.FromResult(_credentials.RetrieveCredential(ListenBrainzTokenResource)?.Password);

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
            return ConnectLastFm(signedIn.Username, signedIn.SessionKey)
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

    private bool ConnectLastFm(string username, string sessionKey)
    {
        if (!SaveCredential(ScrobblingAccount.LastFm, LastFmCredentialResource, username, sessionKey)) return false;
        _settingsService.Set(CoreSettings.LastFmScrobbling, true);
        _settingsService.Set(CoreSettings.LastFmNowPlaying, true);
        return true;
    }

    public Task DisconnectLastFmAsync()
    {
        CancelLastFmSignIn();
        RemoveCredential(ScrobblingAccount.LastFm, LastFmCredentialResource);
        _settingsService.Set(CoreSettings.LastFmScrobbling, false);
        _settingsService.Set(CoreSettings.LastFmNowPlaying, false);
        return Task.CompletedTask;
    }

    public Task<bool> ConnectListenBrainzAsync(string token)
    {
        if (!SaveCredential(ScrobblingAccount.ListenBrainz, ListenBrainzTokenResource, ListenBrainzTokenUserName, token))
            return Task.FromResult(false);
        // Sending every listen ever made would flood the account with history it may already have.
        if (_settingsService.Get(CoreSettings.ListenBrainzEnabledSinceUtc) is null)
            _settingsService.Set(CoreSettings.ListenBrainzEnabledSinceUtc, DateTime.UtcNow);
        return Task.FromResult(true);
    }

    public Task DisconnectListenBrainzAsync()
    {
        RemoveCredential(ScrobblingAccount.ListenBrainz, ListenBrainzTokenResource);
        _settingsService.Set(CoreSettings.ListenBrainzScrobbling, false);
        _settingsService.Set(CoreSettings.ListenBrainzNowPlaying, false);
        _settingsService.Set(CoreSettings.ListenBrainzEnabledSinceUtc, null);
        return Task.CompletedTask;
    }

    private bool SaveCredential(ScrobblingAccount account, string resource, string userName, string secret)
    {
        if (!_credentials.SaveCredential(resource, userName, secret))
        {
            _logger.LogWarning("The credential store refused the sign-in for {Resource}.", resource);
            return false;
        }

        CredentialsChanged?.Invoke(account);
        return true;
    }

    private void RemoveCredential(ScrobblingAccount account, string resource)
    {
        _credentials.RemoveCredential(resource);
        CredentialsChanged?.Invoke(account);
    }
}
