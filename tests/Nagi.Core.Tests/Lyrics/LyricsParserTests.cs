using FluentAssertions;
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
