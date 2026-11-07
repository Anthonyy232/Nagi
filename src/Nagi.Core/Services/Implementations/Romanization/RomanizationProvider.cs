using System.Text.Json;
using Nagi.Core.Services.Abstractions;

namespace Nagi.Core.Services.Implementations.Romanization;

internal abstract class RomanizationProvider<TRules> : IRomanizationProvider where TRules : class
{
    public abstract string EngineId { get; }

    public abstract string RulesFileName { get; }

    public abstract bool Supports(string text);

    public object ParseRules(string json) =>
        JsonSerializer.Deserialize<TRules>(json, RomanizationJson.Options)
        ?? throw new JsonException("Romanization rules must be a JSON object.");

    public string Romanize(string text, object rules) => Romanize(text, (TRules)rules);

    protected abstract string Romanize(string text, TRules rules);

    protected static bool TryMatchLongest(
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

    protected static string NormalizePunctuationSpacing(string text)
    {
        return text
            .Replace(" ,", ",", StringComparison.Ordinal)
            .Replace(" .", ".", StringComparison.Ordinal)
            .Replace(" !", "!", StringComparison.Ordinal)
            .Replace(" ?", "?", StringComparison.Ordinal);
    }
}
