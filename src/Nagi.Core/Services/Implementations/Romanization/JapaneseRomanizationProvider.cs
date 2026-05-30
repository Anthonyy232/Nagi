using System.Text;
using Nagi.Core.Models.Romanization;
using Nagi.Core.Services.Abstractions;

namespace Nagi.Core.Services.Implementations.Romanization;

public sealed class JapaneseRomanizationProvider : IRomanizationProvider
{
    private const string RulesFileName = "japanese-rules.json";

    public string EngineId => "japanese-v1";

    public bool Supports(string text)
    {
        return text.Any(c => c is >= '\u3040' and <= '\u30FF' or >= '\u3400' and <= '\u9FFF');
    }

    public async Task<string?> RomanizeAsync(string text, InstalledRomanizationPack pack, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text) || !Supports(text)) return null;

        var rules = await LoadRulesAsync(pack, cancellationToken).ConfigureAwait(false);
        var result = Romanize(text, rules);
        return string.Equals(result, text, StringComparison.Ordinal) ? null : result;
    }

    public Task<bool> ValidatePackAsync(InstalledRomanizationPack pack, CancellationToken cancellationToken = default)
    {
        return RomanizationProviderHelpers.ValidatePackAsync(pack, RulesFileName, RomanizeAsync, cancellationToken);
    }

    private static Task<JapaneseRuleSet> LoadRulesAsync(InstalledRomanizationPack pack, CancellationToken cancellationToken)
    {
        return RomanizationProviderHelpers.LoadRulesAsync(pack, RulesFileName, () => new JapaneseRuleSet(), cancellationToken);
    }

    private static string Romanize(string text, JapaneseRuleSet rules)
    {
        if (rules.Phrases.TryGetValue(text, out var wholePhrase)) return wholePhrase;

        var builder = new StringBuilder(text.Length * 2);
        var geminateNext = false;
        var index = 0;

        while (index < text.Length)
        {
            if (RomanizationProviderHelpers.TryMatchLongest(text, index, rules.Phrases, out var matchedLength, out var replacement)
                || RomanizationProviderHelpers.TryMatchLongest(text, index, rules.Sequences, out matchedLength, out replacement, NormalizeKana))
            {
                AppendReplacement(builder, replacement, ref geminateNext);
                index += matchedLength;
                continue;
            }

            var current = text[index];
            if (current is '\u3063' or '\u30C3')
            {
                geminateNext = true;
                index++;
                continue;
            }

            if (current == '\u30FC')
            {
                var vowel = FindLastVowel(builder);
                if (vowel is not null) builder.Append(vowel.Value);
                index++;
                continue;
            }

            var normalizedCurrent = NormalizeKana(current).ToString();
            if (rules.Characters.TryGetValue(normalizedCurrent, out replacement)
                || rules.Characters.TryGetValue(current.ToString(), out replacement))
            {
                AppendReplacement(builder, replacement, ref geminateNext);
            }
            else
            {
                builder.Append(current);
                geminateNext = false;
            }

            index++;
        }

        return RomanizationProviderHelpers.NormalizePunctuationSpacing(builder.ToString());
    }

    private static void AppendReplacement(StringBuilder builder, string replacement, ref bool geminateNext)
    {
        if (geminateNext && replacement.Length > 0 && IsGeminationCandidate(replacement[0]))
            builder.Append(replacement[0]);

        builder.Append(replacement);
        geminateNext = false;
    }

    private static string NormalizeKana(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
            builder.Append(NormalizeKana(c));
        return builder.ToString();
    }

    private static char NormalizeKana(char c)
    {
        return c is >= '\u30A1' and <= '\u30F6'
            ? (char)(c - 0x60)
            : c;
    }

    private static bool IsGeminationCandidate(char c)
    {
        return char.IsAsciiLetterLower(c) && c is not 'a' and not 'i' and not 'u' and not 'e' and not 'o' and not 'n';
    }

    private static char? FindLastVowel(StringBuilder builder)
    {
        for (var i = builder.Length - 1; i >= 0; i--)
        {
            var c = builder[i];
            if (c is 'a' or 'i' or 'u' or 'e' or 'o') return c;
        }

        return null;
    }

    private sealed class JapaneseRuleSet
    {
        public Dictionary<string, string> Phrases { get; set; } = new();
        public Dictionary<string, string> Sequences { get; set; } = new();
        public Dictionary<string, string> Characters { get; set; } = new();
    }
}
