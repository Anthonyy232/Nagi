using System.Text;

namespace Nagi.Core.Platform;

public static class TextNormalizer
{
    public static string? Normalize(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : text.Normalize(NormalizationForm.FormC).Trim();
}
