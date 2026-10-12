namespace Nagi.Core.Services.Abstractions;

public interface ILastFmAuthService
{
    /// <summary>
    ///     Gets a new request token from Last.fm and constructs the user authorization URL.
    /// </summary>
    /// <returns>A tuple containing the temporary token and the URL for the user to visit, or null on failure.</returns>
    Task<(string Token, string AuthUrl)?> GetAuthenticationTokenAsync();

    /// <summary>
    ///     Exchanges an authorized token for a long-lived session key.
    /// </summary>
    /// <returns>A tuple containing the username and the session key, or null on failure.</returns>
    Task<(string Username, string SessionKey)?> GetSessionAsync(string token);
}
