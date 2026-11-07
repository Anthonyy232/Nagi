namespace Nagi.Core.Services.Abstractions;

/// <summary>
///     Connects and disconnects the scrobbling accounts, Last.fm and ListenBrainz, keeping their settings in step with
///     their sign-in.
/// </summary>
public interface IScrobblingAccounts
{
    /// <summary>
    ///     Saves the Last.fm session and turns scrobbling and "now playing" on.
    /// </summary>
    /// <returns>False if the credential store refused the session.</returns>
    Task<bool> ConnectLastFmAsync(string username, string sessionKey);

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
