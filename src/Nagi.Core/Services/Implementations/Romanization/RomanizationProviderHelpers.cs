using System.Text.Json;
using Nagi.Core.Models.Romanization;

namespace Nagi.Core.Services.Implementations.Romanization;

internal static class RomanizationProviderHelpers
{
    private static string GetRulesPath(InstalledRomanizationPack pack, string rulesFileName)
    {
        return Path.Combine(pack.DirectoryPath, "data", rulesFileName);
    }

    public static async Task<T> LoadRulesAsync<T>(
        InstalledRomanizationPack pack,
        string rulesFileName,
        CancellationToken cancellationToken) where T : class
    {
        await using var stream = File.OpenRead(GetRulesPath(pack, rulesFileName));
        return await JsonSerializer.DeserializeAsync<T>(stream, RomanizationJson.Options, cancellationToken)
            .ConfigureAwait(false) ?? throw new JsonException("Romanization rules must be a JSON object.");
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

        await using var stream = File.OpenRead(goldenPath);
        var cases = await JsonSerializer.DeserializeAsync<List<RomanizationGoldenCase>>(
            stream, RomanizationJson.Options, cancellationToken).ConfigureAwait(false) ?? new List<RomanizationGoldenCase>();

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

        foreach (var pair in map)
        {
            var key = pair.Key;
            if (key.Length <= matchedLength || start + key.Length > text.Length) continue;

            var candidate = text.AsSpan(start, key.Length);
            var matches = normalizeCandidate is null
                ? candidate.SequenceEqual(key)
                : string.Equals(normalizeCandidate(candidate.ToString()), key, StringComparison.Ordinal);
            if (!matches) continue;

            matchedLength = key.Length;
            replacement = pair.Value;
        }

        return matchedLength > 0;
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
