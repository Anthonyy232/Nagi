namespace Nagi.Core.Lyrics;

/// <param name="Text">The word as it appears in its line's text, including the whitespace that separates it from its neighbour.</param>
public sealed record LyricWord(TimeSpan StartTime, string Text);
