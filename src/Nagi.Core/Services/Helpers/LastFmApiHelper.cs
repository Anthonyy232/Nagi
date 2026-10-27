namespace Nagi.Core.Services.Helpers;

using System.Security.Cryptography;
using System.Text;

public static class LastFmApiHelper
{
    /// <summary>
    ///     Creates the required MD5 signature for an authenticated Last.fm API call.
    ///     Parameters are sorted alphabetically, concatenated, and hashed with the secret.
    /// </summary>
    /// <returns>The MD5 hash signature as a lowercase hex string.</returns>
    public static string CreateSignature(IDictionary<string, string> parameters, string secret)
    {
        var sb = new StringBuilder();

        foreach (var kvp in parameters.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            sb.Append(kvp.Key);
            sb.Append(kvp.Value);
        }

        sb.Append(secret);

        var inputBytes = Encoding.UTF8.GetBytes(sb.ToString());
        var hashBytes = MD5.HashData(inputBytes);

        return Convert.ToHexStringLower(hashBytes);
    }
}
