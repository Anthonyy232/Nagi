namespace Nagi.Core.Models.Lyrics;

public class LyricLine
{
    public LyricLine()
    {
    }

    public LyricLine(TimeSpan startTime, string text)
    {
        StartTime = startTime;
        Text = text;
    }

    /// <summary>
    ///     The timestamp at which this lyric line should be displayed.
    /// </summary>
    public TimeSpan StartTime { get; set; }

    public string Text { get; set; } = string.Empty;

    /// <summary>
    ///     Optional romanized display text. This is UI-only derived data and is never written back to lyric sources.
    /// </summary>
    public string? RomanizedText { get; set; }

    public bool HasRomanizedText => !string.IsNullOrWhiteSpace(RomanizedText);
}
