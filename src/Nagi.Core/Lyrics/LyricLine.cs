namespace Nagi.Core.Lyrics;

public class LyricLine
{
    public LyricLine(TimeSpan startTime, string text)
    {
        StartTime = startTime;
        Text = text;
    }

    public TimeSpan StartTime { get; set; }

    public string Text { get; set; } = string.Empty;

    /// <summary>
    ///     Optional romanized display text. This is UI-only derived data and is never written back to lyric sources.
    /// </summary>
    public string? RomanizedText { get; set; }

    public bool HasRomanizedText => !string.IsNullOrWhiteSpace(RomanizedText);

    /// <summary>
    ///     Further source lines that share this line's timestamp, usually a translation, joined by newlines.
    /// </summary>
    public string? TranslatedText { get; set; }

    public bool HasTranslatedText => !string.IsNullOrWhiteSpace(TranslatedText);

    /// <summary>
    ///     The words of <see cref="Text" /> in order, which join back into it, when the source times each word; empty
    ///     otherwise.
    /// </summary>
    public IReadOnlyList<LyricWord> Words { get; init; } = [];

    /// <summary>
    ///     How many characters of <see cref="Text" /> have been sung at <paramref name="position" />, and when the next
    ///     word starts, which is null once the last one has.
    /// </summary>
    public (int SungLength, TimeSpan? NextWordStart) GetSungText(TimeSpan position)
    {
        var sungLength = 0;
        foreach (var word in Words)
        {
            if (word.StartTime > position) return (sungLength, word.StartTime);
            sungLength += word.Text.Length;
        }

        return (sungLength, null);
    }
}
