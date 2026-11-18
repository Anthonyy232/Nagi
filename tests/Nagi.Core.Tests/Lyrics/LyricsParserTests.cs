using AwesomeAssertions;
using Nagi.Core.Lyrics;
using Xunit;

namespace Nagi.Core.Tests.Lyrics;

public class LyricsParserTests
{
    [Fact]
    public void ParsePlainLyrics_KeepsOneBlankLineBetweenVerses()
    {
        var result = LyricsParser.Parse("First\r\n\r\n\r\n Second ");

        result!.IsSynced.Should().BeFalse();
        result.Lines.Select(line => line.Text).Should().Equal("First", "", "Second");
    }

    [Fact]
    public void ParseLyrics_WithLanguagePrefix_StripsPrefix()
    {
        var content = "eng||[00:01.00]Line 1\n[00:02.00]Line 2";

        var result = LyricsParser.Parse(content)!;

        result.Should().NotBeNull();
        result.Lines.Should().HaveCount(2);
        result.Lines[0].Text.Should().Be("Line 1");
        result.Lines[0].StartTime.Should().Be(TimeSpan.FromSeconds(1));
        result.Lines[1].Text.Should().Be("Line 2");
        result.Lines[1].StartTime.Should().Be(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void GetCurrentLine_WithNoLines_ReturnsNull()
    {
        var hint = 0;
        var result = LyricsParser.GetCurrentLine([], TimeSpan.FromSeconds(5), ref hint);
        result.Should().BeNull();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void GetCurrentLine_WithTranslatedLines_SelectsOriginalLine(int hint)
    {
        var parsed = LyricsParser.Parse("[00:01.00]First\n[00:01.00]Translation\n[00:02.00]Second\n[00:03.00]Third")!;

        var line = LyricsParser.GetCurrentLine(parsed.Lines, TimeSpan.FromSeconds(1), ref hint);

        line!.Text.Should().Be("First");
        hint.Should().Be(0);
    }

    [Fact]
    public void ParseLyrics_WithRepeatedTimestamps_MergesLaterLinesAsTranslation()
    {
        var content = string.Join('\n',
            "[00:01.00][00:09.00]Chorus",
            "[00:03.00]Verse",
            "[00:03.00]",
            "[00:03.00]Verse translation",
            "[00:03.00]Verse transliteration",
            "[00:05.00]Untranslated",
            "[00:01.00][00:09.00]Chorus translation");

        var result = LyricsParser.Parse(content)!;

        result.Lines.Select(line => (line.StartTime.TotalSeconds, line.Text, line.TranslatedText)).Should().Equal(
            (1, "Chorus", "Chorus translation"),
            (3, "Verse", "Verse translation\nVerse transliteration"),
            (5, "Untranslated", null),
            (9, "Chorus", "Chorus translation"));
    }

    [Fact]
    public void ParseLyrics_WithWordTimestamps_KeepsWordsOfTheOriginalLineWithOffsetApplied()
    {
        var content = string.Join('\n',
            "[offset:500]",
            "[00:10.00]<00:10.00>Hello <00:10.50>big <00:11.00>world",
            "[00:10.00]Translation",
            "[00:12.00]Untimed words");

        var result = LyricsParser.Parse(content)!;

        result.Lines[0].Text.Should().Be("Hello big world");
        result.Lines[0].TranslatedText.Should().Be("Translation");
        result.Lines[0].Words.Should().Equal(
            new LyricWord(TimeSpan.FromSeconds(9.5), "Hello "),
            new LyricWord(TimeSpan.FromSeconds(10), "big "),
            new LyricWord(TimeSpan.FromSeconds(10.5), "world"));
        result.Lines[1].Words.Should().BeEmpty();
    }

    [Theory]
    [InlineData(9.9, 0, 10.0)]
    [InlineData(10, 6, 10.5)]
    [InlineData(11, 15, null)]
    public void GetSungText_CountsWordsThatHaveStarted(double positionSec, int expectedLength, double? expectedNextSec)
    {
        var line = LyricsParser.Parse("[00:10.00]<00:10.00>Hello <00:10.50>big <00:11.00>world")!.Lines[0];

        var (sungLength, nextWordStart) = line.GetSungText(TimeSpan.FromSeconds(positionSec));

        sungLength.Should().Be(expectedLength);
        nextWordStart.Should().Be(expectedNextSec is { } seconds ? TimeSpan.FromSeconds(seconds) : null);
    }

    [Fact]
    public void ParseTtml_WithTimedSpans_SplitsWordsOnWhitespaceAndKeepsSyllablesTogether()
    {
        const string content = """
            <?xml version="1.0" encoding="utf-8"?>
            <tt xmlns="http://www.w3.org/ns/ttml" xmlns:ttm="http://www.w3.org/ns/ttml#metadata">
              <body>
                <div>
                  <p begin="1:05.250" end="1:08.000">
                    <span begin="1:05.250" end="1:05.600">Beau</span><span begin="1:05.600" end="1:06.000">ti</span><span begin="1:06.000" end="1:06.400">ful</span>
                    <span begin="66.5s" end="67s">day</span>
                    <span ttm:role="x-bg"><span begin="1:07.500" end="1:08.000">(oh)</span></span>
                    <span ttm:role="x-translation" xml:lang="fr">Belle
                      journée</span>
                    <span ttm:role="x-roman">byutifuru</span>
                  </p>
                  <p begin="10.5" end="12">Line timed only</p>
                </div>
              </body>
            </tt>
            """;

        var result = LyricsParser.Parse(content)!;

        result.IsSynced.Should().BeTrue();
        result.RawLyrics.Should().Be(content);
        result.Lines.Select(line => (line.StartTime.TotalSeconds, line.Text)).Should().Equal(
            (10.5, "Line timed only"),
            (65.25, "Beautiful day (oh)"));
        result.Lines[0].Words.Should().BeEmpty();
        result.Lines[1].TranslatedText.Should().Be("Belle journée");
        result.Lines[1].Words.Should().Equal(
            new LyricWord(TimeSpan.FromSeconds(65.25), "Beau"),
            new LyricWord(TimeSpan.FromSeconds(65.6), "ti"),
            new LyricWord(TimeSpan.FromSeconds(66), "ful"),
            new LyricWord(TimeSpan.FromSeconds(66.5), " day"),
            new LyricWord(TimeSpan.FromSeconds(67.5), " (oh)"));
    }

    [Fact]
    public void ParseTtml_WithoutTimes_GivesPlainLines()
    {
        var result = LyricsParser.Parse("<tt xmlns=\"http://www.w3.org/ns/ttml\"><body><div><p>First</p><p begin=\"12f\">Second</p></div></body></tt>")!;

        result.IsSynced.Should().BeFalse();
        result.Lines.Select(line => line.Text).Should().Equal("First", "Second");
    }

    [Theory]
    [InlineData("<3 this song\nSecond line")]
    [InlineData("<lyrics><p begin=\"1s\">Not TTML</p></lyrics>")]
    public void ParseLyrics_StartingWithAngleBracketButNotTtml_KeepsTheTextAsPlainLines(string content)
    {
        var result = LyricsParser.Parse(content)!;

        result.IsSynced.Should().BeFalse();
        string.Join('\n', result.Lines.Select(line => line.Text)).Should().Be(content);
    }

    private static List<LyricLine> CreateTestLines() =>
    [
        new(TimeSpan.FromSeconds(10), "Line 1"),
        new(TimeSpan.FromSeconds(20), "Line 2"),
        new(TimeSpan.FromSeconds(30), "Line 3")
    ];

    [Theory]
    [InlineData(5, null)]
    [InlineData(10, "Line 1")]
    [InlineData(15, "Line 1")]
    [InlineData(29.9, "Line 2")]
    [InlineData(30, "Line 3")]
    [InlineData(100, "Line 3")]
    public void GetCurrentLine_Simple_ReturnsCorrectLineForTime(double currentTimeSec, string? expectedText)
    {
        var lines = CreateTestLines();
        var currentTime = TimeSpan.FromSeconds(currentTimeSec);

        var hint = 0;
        var result = LyricsParser.GetCurrentLine(lines, currentTime, ref hint);

        (result?.Text).Should().Be(expectedText);
    }

    [Theory]
    [InlineData(1, 25, "Line 2", 1)]
    [InlineData(0, 35, "Line 3", 2)]
    [InlineData(2, 12, "Line 1", 0)]
    public void GetCurrentLine_WithHint_FindsLineAndUpdatesHint(
        int searchIndex, double currentTimeSec, string expectedText, int expectedIndex)
    {
        var lines = CreateTestLines();

        var result = LyricsParser.GetCurrentLine(lines, TimeSpan.FromSeconds(currentTimeSec), ref searchIndex);

        result!.Text.Should().Be(expectedText);
        searchIndex.Should().Be(expectedIndex);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ParseLyrics_WithNullOrWhitespace_ReturnsNull(string? content)
    {
        LyricsParser.Parse(content).Should().BeNull();
    }

    [Fact]
    public void ParseLyrics_WithThreeDigitMilliseconds_PreservesPrecision()
    {
        var content = "[00:01.123]First Line\n[00:05.456]Second Line";

        var result = LyricsParser.Parse(content)!;

        result.IsSynced.Should().BeTrue();
        result.Lines.Should().HaveCount(2);
        result.Lines[0].Text.Should().Be("First Line");
        result.Lines[0].StartTime.Should().Be(TimeSpan.FromMilliseconds(1123));
        result.Lines[1].StartTime.Should().Be(TimeSpan.FromMilliseconds(5456));
    }

    [Fact]
    public void ParseLyrics_WithRepeatedTimestampsAndOffset_ExpandsAndAppliesOffset()
    {
        var content = "[offset:250]\n[00:01.00][00:02.00]Same Line";

        var result = LyricsParser.Parse(content)!;

        result.Lines.Should().HaveCount(2);
        result.Lines.Select(line => line.Text).Should().Equal("Same Line", "Same Line");
        result.Lines.Select(line => line.StartTime).Should().Equal(
            TimeSpan.FromMilliseconds(750),
            TimeSpan.FromMilliseconds(1750));
    }
}
