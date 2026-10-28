namespace Nagi.Core.Http;

/// <summary>
///     Identifies Nagi on every HTTP request. MusicBrainz, Wikimedia and LRCLIB ask clients for a descriptive
///     User-Agent with a contact link.
/// </summary>
internal static class ProviderUserAgent
{
    public const string Value = "Nagi/1.0 (+https://github.com/Anthonyy232/Nagi)";
}
