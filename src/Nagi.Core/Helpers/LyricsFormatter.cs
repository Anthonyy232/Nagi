using ModernLrc;

namespace Nagi.Core.Helpers;

internal static class LyricsFormatter
{
    /// <summary>
    ///     Writes lyric lines as LRC when every line has a start time, and as plain text otherwise.
    /// </summary>
    /// <returns>The lyrics, or null when no line has text.</returns>
    public static string? Format(IReadOnlyCollection<(TimeSpan? Start, string? Text)> lines)
    {
        if (lines.Any(line => line.Start is null))
        {
            var text = string.Join('\n', lines.Select(line => line.Text)).Trim();
            return text.Length == 0 ? null : text;
        }

        var builder = new LrcDocumentBuilder();
        foreach (var (start, text) in lines)
        {
            var normalized = ArtistNameHelper.NormalizeStringCore(text);
            if (normalized is not null) builder.AddLine(start!.Value, normalized);
        }

        return builder.LineCount == 0
            ? null
            : LrcWriter.Write(builder.Build(), new LrcWriteOptions { TimestampPrecision = LrcTimestampPrecision.Milliseconds });
    }
}
