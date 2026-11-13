using Nagi.Core.Platform;

namespace Nagi.Core.OnlineProviders;

/// <summary>
///     Identifies Nagi on every HTTP request. MusicBrainz, Wikimedia and LRCLIB ask clients for a descriptive
///     User-Agent with the application's version and a contact link.
/// </summary>
internal static class ProviderUserAgent
{
    public static string For(string appVersion) => $"Nagi/{appVersion} (+{AppLinks.Repository})";
}
