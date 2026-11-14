using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Nagi.Core.Lyrics;

/// <summary>
///     Reads TTML lyrics: a paragraph is a line, and the timed spans inside it are its words.
/// </summary>
internal static class TtmlLyricsParser
{
    private static readonly XName Role = XName.Get("role", "http://www.w3.org/ns/ttml#metadata");

    /// <returns>Null when the text isn't a TTML document.</returns>
    public static SongLyrics? Parse(string rawLyrics)
    {
        if (!rawLyrics.AsSpan().TrimStart().StartsWith("<")) return null;

        XDocument document;
        try
        {
            // Whitespace between two spans is what tells separate words from syllables of one word.
            document = XDocument.Parse(rawLyrics, LoadOptions.PreserveWhitespace);
        }
        catch (XmlException)
        {
            return null;
        }

        if (document.Root?.Name.LocalName != "tt") return null;

        var lines = document.Descendants()
            .Where(element => element.Name.LocalName == "p")
            .Select(CreateLine)
            .Where(line => line.Line.Text.Length > 0)
            .ToList();

        var timedLines = lines
            .Where(line => line.IsTimed)
            .Select(line => line.Line)
            .OrderBy(line => line.StartTime)
            .ToList();
        return timedLines.Count > 0
            ? new SongLyrics(rawLyrics, timedLines, IsSynced: true)
            : new SongLyrics(rawLyrics, lines.Select(line => line.Line).ToList(), IsSynced: false);
    }

    private static (LyricLine Line, bool IsTimed) CreateLine(XElement paragraph)
    {
        var text = new StringBuilder();
        var wordStarts = new List<(TimeSpan Time, int Offset)>();
        string? translation = null;
        var isSpacePending = false;

        void Append(string value)
        {
            foreach (var character in value)
            {
                if (char.IsWhiteSpace(character))
                {
                    isSpacePending = true;
                    continue;
                }

                if (isSpacePending && text.Length > 0) text.Append(' ');
                isSpacePending = false;
                text.Append(character);
            }
        }

        void Visit(XElement element)
        {
            foreach (var node in element.Nodes())
            {
                if (node is XText textNode)
                {
                    Append(textNode.Value);
                    continue;
                }

                if (node is not XElement child) continue;

                switch (child.Attribute(Role)?.Value)
                {
                    case "x-translation":
                        translation = string.Join(' ', child.Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
                        continue;
                    // Romanization is generated from the text when the user turns it on.
                    case "x-roman":
                        continue;
                }

                if (!child.HasElements && ParseTime(child.Attribute("begin")?.Value) is { } wordStart)
                    wordStarts.Add((wordStart, text.Length));
                Visit(child);
            }
        }

        Visit(paragraph);

        var lineText = text.ToString();
        var words = wordStarts
            .Select((word, index) =>
            {
                // The first word takes any text before it, so the words join back into the whole line.
                var start = index == 0 ? 0 : word.Offset;
                var end = index + 1 < wordStarts.Count ? wordStarts[index + 1].Offset : lineText.Length;
                return new LyricWord(word.Time, lineText[start..end]);
            })
            .ToList();

        var startTime = ParseTime(paragraph.Attribute("begin")?.Value) ?? (words.Count > 0 ? words[0].StartTime : null);
        var line = new LyricLine(startTime ?? TimeSpan.Zero, lineText) { TranslatedText = translation, Words = words };
        return (line, startTime is not null);
    }

    /// <summary>
    ///     Reads the times lyrics are written with: a clock time (<c>1:02:03.5</c>, <c>02:03.5</c>) or seconds
    ///     (<c>3.5</c>, <c>3.5s</c>). TTML's other units give null.
    /// </summary>
    private static TimeSpan? ParseTime(string? value)
    {
        if (value is null) return null;

        var seconds = 0.0;
        foreach (var part in value.TrimEnd('s').Split(':'))
        {
            if (!double.TryParse(part, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number)) return null;
            seconds = seconds * 60 + number;
        }

        return TimeSpan.FromSeconds(seconds);
    }
}
