using System.Text.Json;

namespace Nagi.Core.Services.Implementations.Romanization;

internal static class RomanizationJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };
}
