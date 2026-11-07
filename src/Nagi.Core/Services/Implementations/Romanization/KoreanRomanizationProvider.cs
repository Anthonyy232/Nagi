using System.Text;

namespace Nagi.Core.Services.Implementations.Romanization;

internal sealed class KoreanRomanizationProvider : RomanizationProvider<KoreanRomanizationProvider.HangulRuleSet>
{
    private const int HangulBase = 0xAC00;
    private const int HangulEnd = 0xD7A3;
    private const int InitialCount = 19;
    private const int VowelCount = 21;
    private const int FinalCount = 28;
    private const int SilentInitialIndex = 11;
    private const int FinalIeungIndex = 21;

    public override string RulesFileName => "hangul-rules.json";

    public override string EngineId => "hangul-v1";

    public override bool Supports(string text)
    {
        return text.Any(c => IsHangulSyllable(c) || c is >= '\u3130' and <= '\u318F');
    }

    protected override string Romanize(string text, HangulRuleSet rules)
    {
        if (!HasCompleteTables(rules)) return text;
        if (rules.Phrases.TryGetValue(text, out var wholePhrase)) return wholePhrase;

        var builder = new StringBuilder(text.Length * 2);
        var index = 0;

        while (index < text.Length)
        {
            if (TryMatchLongest(text, index, rules.Phrases, out var phraseLength, out var phraseReplacement))
            {
                builder.Append(phraseReplacement);
                index += phraseLength;
                continue;
            }

            if (TryAppendHangulSyllable(text, index, rules, builder))
            {
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

        return NormalizePunctuationSpacing(builder.ToString());
    }

    private static bool TryAppendHangulSyllable(string text, int index, HangulRuleSet rules, StringBuilder builder)
    {
        var current = text[index];
        if (!IsHangulSyllable(current)) return false;

        Decompose(current, out var initialIndex, out var vowelIndex, out var finalIndex);

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

    internal sealed class HangulRuleSet
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
