using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;

namespace Nagi.Core.Services.Implementations.MusicServers;

/// <summary>
///     Everything that differs between kinds of music server, apart from the client itself: how an address is written,
///     how to sign in, and what is kept of the sign-in.
/// </summary>
internal abstract class MusicServerConnector
{
    public abstract bool SupportsApiKey { get; }

    /// <summary>
    ///     Turns what the user typed into the server's base URL.
    /// </summary>
    /// <exception cref="Data.MusicServerException">The address can't be a server URL.</exception>
    public virtual string NormalizeAddress(string address) => MusicServerHttp.NormalizeAddress(address);

    /// <summary>
    ///     Signs in to <paramref name="server" /> and checks it can be synced.
    /// </summary>
    /// <param name="secret">The password, or the API key for a server that <see cref="MusicServer.UsesApiKey" />.</param>
    /// <exception cref="Data.MusicServerException">The server couldn't be signed in to or isn't supported.</exception>
    public abstract Task<ServerSignIn> SignInAsync(HttpClient httpClient, MusicServer server, string secret);

    /// <param name="secret">The <see cref="ServerSignIn.Secret" /> kept from signing in.</param>
    public abstract IMusicServerClient CreateClient(HttpClient httpClient, MusicServer server, string secret);
}

/// <param name="UserName">The account signed in to, which an API key names itself.</param>
/// <param name="UserId">The server's ID for the account, for servers that identify users by ID.</param>
/// <param name="Secret">What to keep in the credential locker: the password or API key, or an access token in their place.</param>
internal sealed record ServerSignIn(string ServerName, string UserName, string? UserId, string Secret);
