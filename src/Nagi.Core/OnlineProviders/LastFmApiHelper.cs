namespace Nagi.Core.OnlineProviders;

internal static class LastFmApiHelper
{
    public const string BaseUrl = "https://ws.audioscrobbler.com/2.0/";

    /// <summary>
    ///     Whether Last.fm's error code blames the API key or its secret, which fetching them again can fix: an
    ///     invalid key (10), an invalid signature (13) or a suspended key (26).
    /// </summary>
    public static bool IsKeyError(int code) => code is 10 or 13 or 26;
}
