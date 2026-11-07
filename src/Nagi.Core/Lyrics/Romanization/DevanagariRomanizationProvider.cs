using System.Text;

namespace Nagi.Core.Lyrics.Romanization;

internal sealed class DevanagariRomanizationProvider : RomanizationProvider<DevanagariRomanizationProvider.DevanagariRuleSet>
{
    public override string RulesFileName => "devanagari-rules.json";

    public override string EngineId => "devanagari-v1";

    public override bool Supports(string text)
    {
        return text.Any(c => c is >= '\u0900' and <= '\u097F');
    }

    protected override string Romanize(string text, DevanagariRuleSet rules)
    {
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

            if (TryReadConsonant(text, index, rules, out var consonantLength, out var consonant))
            {
                var nextIndex = index + consonantLength;
                builder.Append(consonant);

                if (nextIndex < text.Length && text[nextIndex].ToString() == rules.Virama)
                {
                    index = nextIndex + 1;
                    continue;
                }

                if (nextIndex < text.Length && rules.Matras.TryGetValue(text[nextIndex].ToString(), out var matra))
                {
                    builder.Append(matra);
                    index = AppendMarks(text, nextIndex + 1, rules, builder);
                    continue;
                }

                if (!rules.DropFinalInherentA || !IsWordEnd(text, nextIndex))
                    builder.Append(rules.InherentVowel);

                index = AppendMarks(text, nextIndex, rules, builder);
                continue;
            }

            var current = text[index].ToString();
            if (rules.IndependentVowels.TryGetValue(current, out var vowel))
            {
                builder.Append(vowel);
                index = AppendMarks(text, index + 1, rules, builder);
                continue;
            }

            if (rules.Matras.TryGetValue(current, out var standaloneMatra))
            {
                builder.Append(standaloneMatra);
                index = AppendMarks(text, index + 1, rules, builder);
                continue;
            }

            if (rules.Marks.TryGetValue(current, out var mark))
            {
                builder.Append(mark);
                index++;
                continue;
            }

            builder.Append(rules.Punctuation.TryGetValue(current, out var punctuation) ? punctuation : current);
            index++;
        }

        return NormalizePunctuationSpacing(builder.ToString());
    }

    private static bool TryReadConsonant(
        string text,
        int index,
        DevanagariRuleSet rules,
        out int length,
        out string consonant)
    {
        length = 0;
        consonant = string.Empty;

        if (index + 1 < text.Length)
        {
            var nuktaCandidate = text.Substring(index, 2);
            if (rules.NuktaConsonants.TryGetValue(nuktaCandidate, out var nuktaConsonant))
            {
                consonant = nuktaConsonant;
                length = 2;
                return true;
            }
        }

        var current = text[index].ToString();
        if (!rules.Consonants.TryGetValue(current, out var baseConsonant)) return false;

        consonant = baseConsonant;
        length = 1;
        return true;
    }

    private static int AppendMarks(string text, int index, DevanagariRuleSet rules, StringBuilder builder)
    {
        while (index < text.Length && rules.Marks.TryGetValue(text[index].ToString(), out var mark))
        {
            builder.Append(mark);
            index++;
        }

        return index;
    }

    private static bool IsWordEnd(string text, int index)
    {
        return index >= text.Length || text[index] is < '\u0900' or > '\u097F';
    }

    internal sealed class DevanagariRuleSet
    {
        public Dictionary<string, string> Phrases { get; set; } = new();
        public Dictionary<string, string> IndependentVowels { get; set; } = new();
        public Dictionary<string, string> Matras { get; set; } = new();
        public Dictionary<string, string> Consonants { get; set; } = new();
        public Dictionary<string, string> NuktaConsonants { get; set; } = new();
        public Dictionary<string, string> Marks { get; set; } = new();
        public Dictionary<string, string> Punctuation { get; set; } = new();
        public string Virama { get; set; } = "\u094D";
        public string InherentVowel { get; set; } = "a";
        public bool DropFinalInherentA { get; set; } = true;
    }
}
