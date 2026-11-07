namespace Nagi.Core.Services.Abstractions;

public interface ILastFmAuthService
{
    /// <returns>A temporary token and the page where the user authorizes it, or null on failure.</returns>
    Task<(string Token, string AuthUrl)?> GetAuthenticationTokenAsync();

    /// <summary>
    ///     Exchanges a token the user authorized for a session key that doesn't expire.
    /// </summary>
    /// <returns>The user's name and the session key, or null on failure.</returns>
    Task<(string Username, string SessionKey)?> GetSessionAsync(string token);
}
