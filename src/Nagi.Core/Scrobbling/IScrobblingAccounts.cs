using Nagi.Core.Settings;

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

/// <param name="ApprovalPage">Where the user approves the sign-in, or null if it didn't start.</param>
/// <param name="WasCancelled">
///     Whether the sign-in was cancelled or started again meanwhile, rather than Last.fm being out of reach.
/// </param>
public readonly record struct LastFmSignInStart(Uri? ApprovalPage, bool WasCancelled = false);

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

    /// <returns>The username and session key, or null if not signed in.</returns>
    Task<(string? Username, string? SessionKey)?> GetLastFmCredentialsAsync();

    Task<string?> GetListenBrainzUserTokenAsync();

    /// <summary>
    ///     Starts a Last.fm sign-in, which waits in <see cref="CoreSettings.LastFmAuthToken" />, across restarts too,
    ///     until it is finished or cancelled.
    /// </summary>
    Task<LastFmSignInStart> BeginLastFmSignInAsync();

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
