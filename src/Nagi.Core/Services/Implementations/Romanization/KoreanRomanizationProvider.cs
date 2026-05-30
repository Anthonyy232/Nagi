using System.Text;
using Nagi.Core.Models.Romanization;
using Nagi.Core.Services.Abstractions;

namespace Nagi.Core.Services.Implementations.Romanization;

public sealed class KoreanRomanizationProvider : IRomanizationProvider
{
    private const string RulesFileName = "hangul-rules.json";
    private const int HangulBase = 0xAC00;
    private const int HangulEnd = 0xD7A3;
    private const int InitialCount = 19;
    private const int VowelCount = 21;
    private const int FinalCount = 28;
    private const int SilentInitialIndex = 11;
    private const int FinalIeungIndex = 21;

    public string EngineId => "hangul-v1";

    public bool Supports(string text)
    {
        return text.Any(c => IsHangulSyllable(c) || c is >= '\u3130' and <= '\u318F');
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

    private static Task<HangulRuleSet> LoadRulesAsync(InstalledRomanizationPack pack, CancellationToken cancellationToken)
    {
        return RomanizationProviderHelpers.LoadRulesAsync(pack, RulesFileName, () => new HangulRuleSet(), cancellationToken);
    }

    private static string Romanize(string text, HangulRuleSet rules)
    {
        if (!HasCompleteTables(rules)) return text;
        if (rules.Phrases.TryGetValue(text, out var wholePhrase)) return wholePhrase;

        var builder = new StringBuilder(text.Length * 2);
        var index = 0;

        while (index < text.Length)
        {
            if (RomanizationProviderHelpers.TryMatchLongest(text, index, rules.Phrases, out var phraseLength, out var phraseReplacement))
            {
                builder.Append(phraseReplacement);
                index += phraseLength;
                continue;
            }

            if (TryRomanizeHangulSyllable(text, index, rules, out var syllable))
            {
                builder.Append(syllable);
                index++;
                continue;
            }

            var current = text[index].ToString();
            if (rules.Characters.TryGetValue(current, out var character)
                || rules.Punctuation.TryGetValue(current, out character))
            {
                builder.Append(character);
            }
            else
            {
                builder.Append(text[index]);
            }

            index++;
        }

        return RomanizationProviderHelpers.NormalizePunctuationSpacing(builder.ToString());
    }

    private static bool TryRomanizeHangulSyllable(string text, int index, HangulRuleSet rules, out string romanized)
    {
        romanized = string.Empty;
        var current = text[index];
        if (!IsHangulSyllable(current)) return false;

        Decompose(current, out var initialIndex, out var vowelIndex, out var finalIndex);

        var builder = new StringBuilder();
        builder.Append(rules.InitialConsonants[initialIndex]);
        builder.Append(rules.Vowels[vowelIndex]);

        if (finalIndex > 0)
        {
            var nextStartsWithSilentInitial = index + 1 < text.Length
                && IsHangulSyllable(text[index + 1])
                && GetInitialIndex(text[index + 1]) == SilentInitialIndex;

            var finals = nextStartsWithSilentInitial && finalIndex != FinalIeungIndex
                ? rules.FinalConsonantsBeforeVowel
                : rules.FinalConsonants;

            builder.Append(finals[finalIndex]);
        }

        romanized = builder.ToString();
        return true;
    }

    private static void Decompose(char syllable, out int initialIndex, out int vowelIndex, out int finalIndex)
    {
        var offset = syllable - HangulBase;
        initialIndex = offset / (VowelCount * FinalCount);
        vowelIndex = offset % (VowelCount * FinalCount) / FinalCount;
        finalIndex = offset % FinalCount;
    }

    private static int GetInitialIndex(char syllable)
    {
        var offset = syllable - HangulBase;
        return offset / (VowelCount * FinalCount);
    }

    private static bool IsHangulSyllable(char c)
    {
        return c is >= (char)HangulBase and <= (char)HangulEnd;
    }

    private static bool HasCompleteTables(HangulRuleSet rules)
    {
        return rules.InitialConsonants.Count == InitialCount
               && rules.Vowels.Count == VowelCount
               && rules.FinalConsonants.Count == FinalCount
               && rules.FinalConsonantsBeforeVowel.Count == FinalCount;
    }

    private sealed class HangulRuleSet
    {
        public Dictionary<string, string> Phrases { get; set; } = new();
        public List<string> InitialConsonants { get; set; } = new();
        public List<string> Vowels { get; set; } = new();
        public List<string> FinalConsonants { get; set; } = new();
        public List<string> FinalConsonantsBeforeVowel { get; set; } = new();
        public Dictionary<string, string> Characters { get; set; } = new();
        public Dictionary<string, string> Punctuation { get; set; } = new();
    }
}
