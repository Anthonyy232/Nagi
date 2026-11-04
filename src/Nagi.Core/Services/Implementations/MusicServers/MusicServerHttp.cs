using Nagi.Core.Services.Data;

namespace Nagi.Core.Services.Implementations.MusicServers;

internal static class MusicServerHttp
{
    /// <summary>
    ///     Turns what the user typed into a base URL such as <c>https://music.example.com:4533/navidrome</c>.
    ///     An address without a scheme means HTTPS: falling back to HTTP on its own would send the sign-in
    ///     unencrypted, so plain HTTP must be asked for.
    /// </summary>
    /// <exception cref="MusicServerException">The address can't be a server URL.</exception>
    public static string NormalizeAddress(string address)
    {
        var trimmed = address.Trim();
        if (!trimmed.Contains("://", StringComparison.Ordinal)) trimmed = $"https://{trimmed}";

        // Credentials or a query in the address would be stored and logged with it.
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) ||
            uri.Host.Length == 0 || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            throw new MusicServerException(MusicServerError.InvalidAddress, "The address is not a server URL.");

        return uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
    }

    /// <exception cref="MusicServerException">The server couldn't be reached.</exception>
    public static async Task<HttpResponseMessage> SendAsync(HttpClient httpClient, HttpRequestMessage request,
        string baseUrl, CancellationToken cancellationToken)
    {
        try
        {
            return await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            // A home server reached without a scheme often only speaks HTTP, which the user can fix by asking for it.
            var error = ex.HttpRequestError == HttpRequestError.SecureConnectionError
                ? MusicServerError.SecureConnectionFailed
                : MusicServerError.Unreachable;
            throw new MusicServerException(error, $"Couldn't reach {baseUrl}.", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new MusicServerException(MusicServerError.Unreachable, $"{baseUrl} didn't answer in time.", ex);
        }
    }
}
