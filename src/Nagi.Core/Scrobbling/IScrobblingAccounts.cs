namespace Nagi.Core.Scrobbling;

public enum LastFmSignInResult
{
    Connected,

    /// <summary>No sign-in was waiting, or it was cancelled.</summary>
    NotStarted,

    /// <summary>Last.fm couldn't be reached or didn't approve the sign-in.</summary>
    Failed,

    /// <summary>The credential store refused the session.</summary>
    NotSaved
}

/// <summary>
///     Connects and disconnects the scrobbling accounts, Last.fm and ListenBrainz, keeping their settings in step with
///     their sign-in. Their credentials live in the operating system's credential store.
/// </summary>
public interface IScrobblingAccounts
{
    /// <summary>
    ///     Raised when the credential of an account is saved or removed.
    /// </summary>
    event Action<ScrobblingAccount>? CredentialsChanged;

    /// <summary>
    ///     Whether a Last.fm sign-in waits for the user to approve it in the browser, across restarts too.
    /// </summary>
    bool IsLastFmSignInPending { get; }

    /// <returns>The username and session key, or null if not signed in.</returns>
    Task<(string? Username, string? SessionKey)?> GetLastFmCredentialsAsync();

    Task<string?> GetListenBrainzUserTokenAsync();

    /// <summary>
    ///     Starts a Last.fm sign-in, which waits until it is finished or cancelled.
    /// </summary>
    /// <returns>
    ///     The page where the user approves the sign-in, or null if Last.fm couldn't be reached or the sign-in was
    ///     cancelled meanwhile.
    /// </returns>
    Task<Uri?> BeginLastFmSignInAsync();

    /// <summary>
    ///     Exchanges the approved sign-in for a session, then saves it and turns scrobbling and "now playing" on. The
    ///     sign-in ends either way.
    /// </summary>
    Task<LastFmSignInResult> FinishLastFmSignInAsync();

    void CancelLastFmSignIn();

    /// <summary>
    ///     Forgets the Last.fm session and any sign-in in progress, and turns its submissions off.
    /// </summary>
    Task DisconnectLastFmAsync();

    /// <summary>
    ///     Saves the ListenBrainz user token. Listens from before it was first connected aren't sent.
    /// </summary>
    /// <returns>False if the credential store refused the token.</returns>
    Task<bool> ConnectListenBrainzAsync(string token);

    /// <summary>
    ///     Forgets the ListenBrainz token and turns its submissions off, so connecting again starts from then.
    /// </summary>
    Task DisconnectListenBrainzAsync();
}
