using System.Text.Json;

namespace Nagi.Core.Lyrics.Romanization;

internal static class RomanizationJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true
    };
}
