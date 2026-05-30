using System.Text.Json;
using Nagi.Core.Models.Romanization;

namespace Nagi.Core.Services.Implementations.Romanization;

internal static class RomanizationProviderHelpers
{
    public static string GetRulesPath(InstalledRomanizationPack pack, string rulesFileName)
    {
        return Path.Combine(pack.DirectoryPath, "data", rulesFileName);
    }

    public static async Task<T> LoadRulesAsync<T>(
        InstalledRomanizationPack pack,
        string rulesFileName,
        Func<T> createDefault,
        CancellationToken cancellationToken)
    {
        var json = await File.ReadAllTextAsync(GetRulesPath(pack, rulesFileName), cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(json, RomanizationJson.Options) ?? createDefault();
    }

    public static async Task<bool> ValidatePackAsync(
        InstalledRomanizationPack pack,
        string rulesFileName,
        Func<string, InstalledRomanizationPack, CancellationToken, Task<string?>> romanizeAsync,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(GetRulesPath(pack, rulesFileName))) return false;

        var goldenPath = Path.Combine(pack.DirectoryPath, "tests", "golden.json");
        if (!File.Exists(goldenPath)) return true;

        var cases = JsonSerializer.Deserialize<List<RomanizationGoldenCase>>(
            await File.ReadAllTextAsync(goldenPath, cancellationToken).ConfigureAwait(false),
            RomanizationJson.Options) ?? new List<RomanizationGoldenCase>();

        foreach (var testCase in cases)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var actual = await romanizeAsync(testCase.Input, pack, cancellationToken).ConfigureAwait(false)
                         ?? testCase.Input;
            if (!string.Equals(actual, testCase.Expected, StringComparison.Ordinal)) return false;
        }

        return true;
    }

    public static bool TryMatchLongest(
        string text,
        int start,
        IReadOnlyDictionary<string, string> map,
        out int matchedLength,
        out string replacement,
        Func<string, string>? normalizeCandidate = null)
    {
        matchedLength = 0;
        replacement = string.Empty;

        foreach (var pair in map.OrderByDescending(pair => pair.Key.Length))
        {
            var key = pair.Key;
            if (key.Length == 0 || start + key.Length > text.Length) continue;

            var candidate = text.Substring(start, key.Length);
            if (normalizeCandidate is not null) candidate = normalizeCandidate(candidate);
            if (!string.Equals(candidate, key, StringComparison.Ordinal)) continue;

            matchedLength = key.Length;
            replacement = pair.Value;
            return true;
        }

        return false;
    }

    public static string NormalizePunctuationSpacing(string text)
    {
        return text
            .Replace(" ,", ",", StringComparison.Ordinal)
            .Replace(" .", ".", StringComparison.Ordinal)
            .Replace(" !", "!", StringComparison.Ordinal)
            .Replace(" ?", "?", StringComparison.Ordinal);
    }
}
