using FluentAssertions;
using Microsoft.Extensions.Logging;
using Nagi.Core.Data;
using Nagi.Core.Helpers;
using Nagi.Core.Models;
using Nagi.Core.Models.Lyrics;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Implementations;
using NSubstitute;
using Xunit;

namespace Nagi.Core.Tests;

/// <summary>
///     Contains unit tests for the <see cref="LrcService" />.
///     These tests verify the service's ability to parse LRC files from disk and to efficiently
///     determine the currently active lyric line based on a given timestamp.
/// </summary>
public class LrcServiceTests
{
    private const string LrcPath = "C:\\lyrics\\test.lrc";
    private readonly IFileSystemService _fileSystem;
    private readonly IOnlineLyricsService _onlineLyricsService;
    private readonly ISettingsService _settingsService;
    private readonly IPathConfiguration _pathConfig;
    private readonly ILibraryWriter _libraryWriter;
    private readonly ILogger<LrcService> _logger;
    private readonly LrcService _lrcService;

    public LrcServiceTests()
    {
        _fileSystem = Substitute.For<IFileSystemService>();
        _onlineLyricsService = Substitute.For<IOnlineLyricsService>();
        _settingsService = Substitute.For<ISettingsService>();
        _pathConfig = Substitute.For<IPathConfiguration>();
        _libraryWriter = Substitute.For<ILibraryWriter>();
        _logger = Substitute.For<ILogger<LrcService>>();
        _lrcService = new LrcService(
            _fileSystem,
            _onlineLyricsService,
            _settingsService,
            _pathConfig,
            _libraryWriter,
            _logger);
    }

    #region GetLyricsAsync Tests

    /// <summary>
    ///     Verifies that a valid LRC file, even with out-of-order timestamps, is parsed
    ///     correctly and the resulting lines are ordered by their start time.
    /// </summary>
    [Fact]
    public async Task GetLyricsAsync_WithValidLrcFile_ReturnsParsedAndOrderedLyrics()
    {
        // Arrange
        var lrcContent = "[00:05.00]Line 2\n[00:01.00]Line 1";
        _fileSystem.FileExists(LrcPath).Returns(true);
        _fileSystem.ReadAllTextAsync(LrcPath).Returns(lrcContent);

        // Act
        var result = await _lrcService.GetLyricsAsync(LrcPath);

        // Assert
        result.Should().NotBeNull();
        result!.IsEmpty.Should().BeFalse();
        result.Lines.Should().HaveCount(2);
        result.Lines[0].Text.Should().Be("Line 1");
        result.Lines[0].StartTime.Should().Be(TimeSpan.FromSeconds(1));
        result.Lines[1].Text.Should().Be("Line 2");
        result.Lines[1].StartTime.Should().Be(TimeSpan.FromSeconds(5));
    }

    /// <summary>
    ///     Verifies that attempting to get lyrics from a path that does not exist on the
    ///     file system returns null.
    /// </summary>
    [Fact]
    public async Task GetLyricsAsync_WithNonExistentFile_ReturnsNull()
    {
        // Arrange
        _fileSystem.FileExists(LrcPath).Returns(false);

        // Act
        var result = await _lrcService.GetLyricsAsync(LrcPath);

        // Assert
        result.Should().BeNull();
    }

    /// <summary>
    ///     Verifies that providing a null, empty, or whitespace path to <see cref="LrcService.GetLyricsAsync" />
    ///     returns null without attempting any file operations.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GetLyricsAsync_WithInvalidPath_ReturnsNull(string? path)
    {
        // Act
        var result = await _lrcService.GetLyricsAsync(path!);

        // Assert
        result.Should().BeNull();
    }

    /// <summary>
    ///     Verifies that reading an empty LRC file results in a valid, but empty,
    ///     <see cref="ParsedLrc" /> object.
    /// </summary>
    [Fact]
    public async Task GetLyricsAsync_WithEmptyFileContent_ReturnsEmptyParsedLrc()
    {
        // Arrange
        _fileSystem.FileExists(LrcPath).Returns(true);
        _fileSystem.ReadAllTextAsync(LrcPath).Returns(string.Empty);

        // Act
        var result = await _lrcService.GetLyricsAsync(LrcPath);

        // Assert
        result.Should().NotBeNull();
        result!.IsEmpty.Should().BeTrue();
    }

    /// <summary>
    ///     Verifies that getting lyrics from a <see cref="Song" /> object with a valid
    ///     <see cref="Song.LrcFilePath" /> successfully returns the parsed lyrics.
    /// </summary>
    [Fact]
    public async Task GetLyricsAsync_FromSong_WithValidPath_ReturnsLyrics()
    {
        // Arrange
        var song = new Song { LrcFilePath = LrcPath };
        var lrcContent = "[00:01.00]Hello";
        _fileSystem.FileExists(LrcPath).Returns(true);
        _fileSystem.ReadAllTextAsync(LrcPath).Returns(lrcContent);

        // Act
        var result = await _lrcService.GetLyricsAsync(song);

        // Assert
        result.Should().NotBeNull();
        result!.Lines.Should().HaveCount(1);
    }

    /// <summary>
    ///     Verifies that getting lyrics from a <see cref="Song" /> object with a null
    ///     <see cref="Song.LrcFilePath" /> returns null.
    /// </summary>
    [Fact]
    public async Task GetLyricsAsync_FromSong_WithNoPath_ReturnsNull()
    {
        // Arrange
        var song = new Song { LrcFilePath = null };

        // Act
        var result = await _lrcService.GetLyricsAsync(song);

        // Assert
        result.Should().BeNull();
    }

    #endregion

    #region GetCurrentLine Tests

    /// <summary>
    ///     Creates a standard <see cref="ParsedLrc" /> object for use in current line tests.
    /// </summary>
    private static ParsedLrc CreateTestParsedLrc()
    {
        var lines = new List<LyricLine>
        {
            new() { StartTime = TimeSpan.FromSeconds(10), Text = "Line 1" },
            new() { StartTime = TimeSpan.FromSeconds(20), Text = "Line 2" },
            new() { StartTime = TimeSpan.FromSeconds(30), Text = "Line 3" }
        };
        return new ParsedLrc(lines);
    }

    /// <summary>
    ///     Verifies that <see cref="LrcService.GetCurrentLine(ParsedLrc, TimeSpan)" /> returns the
    ///     correct lyric line for various timestamps.
    /// </summary>
    /// <remarks>
    ///     This test covers cases before the first line, exactly on a line's start time, between lines,
    ///     and after the last line.
    /// </remarks>
    [Theory]
    [InlineData(5, null)] // Before first line
    [InlineData(10, "Line 1")] // Exactly on first line
    [InlineData(15, "Line 1")] // Between line 1 and 2
    [InlineData(29.9, "Line 2")] // Just before line 3
    [InlineData(30, "Line 3")] // Exactly on line 3
    [InlineData(100, "Line 3")] // After last line
    public void GetCurrentLine_Simple_ReturnsCorrectLineForTime(double currentTimeSec, string? expectedText)
    {
        // Arrange
        var parsedLrc = CreateTestParsedLrc();
        var currentTime = TimeSpan.FromSeconds(currentTimeSec);

        // Act
        var result = _lrcService.GetCurrentLine(parsedLrc, currentTime);

        // Assert
        result?.Text.Should().Be(expectedText);
    }

    /// <summary>
    ///     Verifies that when a correct search index hint is provided, the service returns the
    ///     correct line efficiently without performing a full search.
    /// </summary>
    [Fact]
    public void GetCurrentLine_WithHint_WhenHintIsCorrect_ReturnsLineWithoutSearching()
    {
        // Arrange
        var parsedLrc = CreateTestParsedLrc();
        var searchIndex = 1; // Hinting that we are on Line 2

        // Act
        // Time is within the hinted line's duration
        var result = _lrcService.GetCurrentLine(parsedLrc, TimeSpan.FromSeconds(25), ref searchIndex);

        // Assert
        result?.Text.Should().Be("Line 2");
        searchIndex.Should().Be(1); // Index should not change
    }

    /// <summary>
    ///     Verifies that when seeking forward in time, the service finds the correct new line
    ///     and updates the search index hint accordingly.
    /// </summary>
    [Fact]
    public void GetCurrentLine_WithHint_WhenSeekingForward_FindsCorrectLineAndUpdatesHint()
    {
        // Arrange
        var parsedLrc = CreateTestParsedLrc();
        var searchIndex = 0; // Hinting we are on Line 1

        // Act
        // We seek forward to a time within Line 3's duration
        var result = _lrcService.GetCurrentLine(parsedLrc, TimeSpan.FromSeconds(35), ref searchIndex);

        // Assert
        result?.Text.Should().Be("Line 3");
        searchIndex.Should().Be(2); // Index should be updated to the new correct line
    }

    /// <summary>
    ///     Verifies that when seeking backward in time, the service finds the correct new line
    ///     and updates the search index hint accordingly.
    /// </summary>
    [Fact]
    public void GetCurrentLine_WithHint_WhenSeekingBackward_FindsCorrectLineAndUpdatesHint()
    {
        // Arrange
        var parsedLrc = CreateTestParsedLrc();
        var searchIndex = 2; // Hinting we are on Line 3

        // Act
        // We seek backward to a time within Line 1's duration
        var result = _lrcService.GetCurrentLine(parsedLrc, TimeSpan.FromSeconds(12), ref searchIndex);

        // Assert
        result?.Text.Should().Be("Line 1");
        searchIndex.Should().Be(0); // Index should be updated
    }

    #endregion
}