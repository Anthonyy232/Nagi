using Nagi.Core.Platform;

namespace Nagi.Core.OnlineProviders;

/// <summary>
///     Identifies Nagi on every HTTP request. MusicBrainz, Wikimedia and LRCLIB ask clients for a descriptive
///     User-Agent with a contact link.
/// </summary>
internal static class ProviderUserAgent
{
    public const string Value = "Nagi/1.0 (+" + AppLinks.Repository + ")";
}
