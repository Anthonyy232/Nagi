using FluentAssertions;
using Microsoft.Extensions.Logging;
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
    private readonly INetEaseLyricsService _netEaseLyricsService;
    private readonly LrcService _lrcService;

    public LrcServiceTests()
    {
        _fileSystem = Substitute.For<IFileSystemService>();
        _onlineLyricsService = Substitute.For<IOnlineLyricsService>();
        _settingsService = Substitute.For<ISettingsService>();
        _pathConfig = Substitute.For<IPathConfiguration>();
        _libraryWriter = Substitute.For<ILibraryWriter>();
        _logger = Substitute.For<ILogger<LrcService>>();
        _netEaseLyricsService = Substitute.For<INetEaseLyricsService>();
        
        // Default mock: return both providers enabled in standard priority order
        _settingsService.GetEnabledServiceProvidersAsync(ServiceCategory.Lyrics)
            .Returns(new List<ServiceProviderSetting>
            {
                new() { Id = "lrclib", DisplayName = "LRCLIB", Category = ServiceCategory.Lyrics, IsEnabled = true, Order = 0 },
                new() { Id = "netease", DisplayName = "NetEase", Category = ServiceCategory.Lyrics, IsEnabled = true, Order = 1 }
            });
        
        _lrcService = new LrcService(
            _fileSystem,
            _onlineLyricsService,
            _netEaseLyricsService,
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

    #region Online Fallback Tests

    /// <summary>
    ///     Verifies that when local lyrics are not available and online fetch is enabled,
    ///     both LRCLIB and NetEase are fetched in parallel, with LRCLIB result preferred.
    /// </summary>
    [Fact]
    public async Task GetLyricsAsync_WithNoLocalFile_FetchesBothInParallel_PrefersLrcLib()
    {
        // Arrange
        var song = new Song { Title = "Test", LrcFilePath = null, Duration = TimeSpan.FromMinutes(3) };
        _settingsService.GetFetchOnlineLyricsEnabledAsync().Returns(true);
        _onlineLyricsService.GetLyricsAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), 
            Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns("[00:01.00]From LRCLIB");
        _netEaseLyricsService.SearchLyricsAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns("[00:01.00]From NetEase");

        // Act
        var result = await _lrcService.GetLyricsAsync(song);

        // Assert - LRCLIB result should be used even though both were called
        result.Should().NotBeNull();
        result!.Lines.Should().HaveCount(1);
        result.Lines[0].Text.Should().Be("From LRCLIB");
        
        // Both services should be called (parallel fetching)
        await _onlineLyricsService.Received(1).GetLyricsAsync(Arg.Any<string>(), Arg.Any<string?>(), 
            Arg.Any<string?>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
        await _netEaseLyricsService.Received(1).SearchLyricsAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    ///     Verifies that when LRCLIB returns no results, the NetEase result (fetched in parallel) is used.
    /// </summary>
    [Fact]
    public async Task GetLyricsAsync_WhenLrcLibReturnsNull_UsesNetEaseResult()
    {
        // Arrange
        var song = new Song { Title = "Japanese Song", LrcFilePath = null, Duration = TimeSpan.FromMinutes(3) };
        _settingsService.GetFetchOnlineLyricsEnabledAsync().Returns(true);
        _onlineLyricsService.GetLyricsAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), 
            Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns((string?)null);
        _netEaseLyricsService.SearchLyricsAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns("[00:01.00]From NetEase");

        // Act
        var result = await _lrcService.GetLyricsAsync(song);

        // Assert - NetEase result used since LRCLIB returned null
        result.Should().NotBeNull();
        result!.Lines[0].Text.Should().Be("From NetEase");
    }

    /// <summary>
    ///     Verifies that when online lyrics fetch is disabled, no remote services are called.
    /// </summary>
    [Fact]
    public async Task GetLyricsAsync_WhenOnlineFetchDisabled_DoesNotCallRemoteServices()
    {
        // Arrange
        var song = new Song { Title = "Test", LrcFilePath = null };
        _settingsService.GetFetchOnlineLyricsEnabledAsync().Returns(false);

        // Act
        var result = await _lrcService.GetLyricsAsync(song);

        // Assert
        result.Should().BeNull();
        await _onlineLyricsService.DidNotReceive().GetLyricsAsync(Arg.Any<string>(), Arg.Any<string?>(), 
            Arg.Any<string?>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
        await _netEaseLyricsService.DidNotReceive().SearchLyricsAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    ///     Verifies that when no lyrics providers are enabled, no remote services are called.
    /// </summary>
    [Fact]
    public async Task GetLyricsAsync_WhenNoProvidersEnabled_DoesNotCallRemoteServices()
    {
        // Arrange
        var song = new Song { Title = "Test", LrcFilePath = null, Duration = TimeSpan.FromMinutes(3) };
        _settingsService.GetFetchOnlineLyricsEnabledAsync().Returns(true);
        _settingsService.GetEnabledServiceProvidersAsync(ServiceCategory.Lyrics)
            .Returns(new List<ServiceProviderSetting>()); // Empty list - no providers enabled

        // Act
        var result = await _lrcService.GetLyricsAsync(song);

        // Assert
        result.Should().BeNull();
        await _onlineLyricsService.DidNotReceive().GetLyricsAsync(Arg.Any<string>(), Arg.Any<string?>(), 
            Arg.Any<string?>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
        await _netEaseLyricsService.DidNotReceive().SearchLyricsAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    ///     Verifies that when only NetEase is enabled, only NetEase is called.
    /// </summary>
    [Fact]
    public async Task GetLyricsAsync_WhenOnlyNetEaseEnabled_OnlyCallsNetEase()
    {
        // Arrange
        var song = new Song { Title = "Test", LrcFilePath = null, Duration = TimeSpan.FromMinutes(3) };
        _settingsService.GetFetchOnlineLyricsEnabledAsync().Returns(true);
        _settingsService.GetEnabledServiceProvidersAsync(ServiceCategory.Lyrics)
            .Returns(new List<ServiceProviderSetting>
            {
                new() { Id = "netease", DisplayName = "NetEase", Category = ServiceCategory.Lyrics, IsEnabled = true, Order = 0 }
            });
        _netEaseLyricsService.SearchLyricsAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns("[00:01.00]From NetEase");

        // Act
        var result = await _lrcService.GetLyricsAsync(song);

        // Assert
        result.Should().NotBeNull();
        result!.Lines[0].Text.Should().Be("From NetEase");
        
        // Verify LRCLIB was NOT called
        await _onlineLyricsService.DidNotReceive().GetLyricsAsync(Arg.Any<string>(), Arg.Any<string?>(), 
            Arg.Any<string?>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
        // Verify NetEase WAS called
        await _netEaseLyricsService.Received(1).SearchLyricsAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    #endregion

    #region LyricsLastChecked Tests

    /// <summary>
    ///     Verifies that LyricsLastCheckedUtc is NOT set when no providers are enabled,
    ///     allowing future retry when providers are enabled.
    /// </summary>
    [Fact]
    public async Task GetLyricsAsync_WhenNoProvidersEnabled_DoesNotSetLyricsLastChecked()
    {
        // Arrange
        var song = new Song { Title = "Test", LrcFilePath = null, Duration = TimeSpan.FromMinutes(3) };
        _settingsService.GetFetchOnlineLyricsEnabledAsync().Returns(true);
        _settingsService.GetEnabledServiceProvidersAsync(ServiceCategory.Lyrics)
            .Returns(new List<ServiceProviderSetting>()); // Empty list

        // Act
        await _lrcService.GetLyricsAsync(song);

        // Assert - LyricsLastCheckedUtc should NOT be set
        await _libraryWriter.DidNotReceive().UpdateSongLyricsLastCheckedAsync(Arg.Any<Guid>());
        song.LyricsLastCheckedUtc.Should().BeNull();
    }

    #endregion

    #region Cancellation Tests

    /// <summary>
    ///     Verifies that when already cancelled, neither LRCLIB nor NetEase is called.
    /// </summary>
    [Fact]
    public async Task GetLyricsAsync_WhenAlreadyCancelled_DoesNotCallOnlineServices()
    {
        // Arrange
        var song = new Song { Title = "Test", LrcFilePath = null, Duration = TimeSpan.FromMinutes(3) };
        _settingsService.GetFetchOnlineLyricsEnabledAsync().Returns(true);
        
        var cts = new CancellationTokenSource();
        cts.Cancel(); // Cancel BEFORE the call

        // Act
        var result = await _lrcService.GetLyricsAsync(song, cts.Token);

        // Assert - Should return null and NOT call any online services
        result.Should().BeNull();
        await _onlineLyricsService.DidNotReceive().GetLyricsAsync(Arg.Any<string>(), Arg.Any<string?>(), 
            Arg.Any<string?>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
        await _netEaseLyricsService.DidNotReceive().SearchLyricsAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    ///     Verifies that local file access takes priority and doesn't trigger online fetch.
    /// </summary>
    [Fact]
    public async Task GetLyricsAsync_WithLocalFile_DoesNotFetchOnline()
    {
        // Arrange
        var song = new Song { Title = "Test", LrcFilePath = LrcPath };
        _fileSystem.FileExists(LrcPath).Returns(true);
        _fileSystem.ReadAllTextAsync(LrcPath).Returns("[00:01.00]Local Lyrics");
        _settingsService.GetFetchOnlineLyricsEnabledAsync().Returns(true);

        // Act
        var result = await _lrcService.GetLyricsAsync(song);

        // Assert
        result.Should().NotBeNull();
        result!.Lines[0].Text.Should().Be("Local Lyrics");
        await _onlineLyricsService.DidNotReceive().GetLyricsAsync(Arg.Any<string>(), Arg.Any<string?>(), 
            Arg.Any<string?>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    ///     Verifies that for a song with multiple artists, online providers are called
    ///     using only the primary artist name.
    /// </summary>
    [Fact]
    public async Task GetLyricsAsync_WithMultiArtistSong_CallsProvidersWithPrimaryArtist()
    {
        // Arrange
        var song = new Song { Title = "Multi Artist Track", Duration = TimeSpan.FromMinutes(3) };
        var artist1 = new Artist { Name = "Primary Artist" };
        var artist2 = new Artist { Name = "Secondary Artist" };
        song.SongArtists.Add(new SongArtist { Song = song, Artist = artist1, Order = 0 });
        song.SongArtists.Add(new SongArtist { Song = song, Artist = artist2, Order = 1 });
        song.SyncDenormalizedFields();

        _settingsService.GetFetchOnlineLyricsEnabledAsync().Returns(true);
        _onlineLyricsService.GetLyricsAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), 
            Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns("[00:01.00]Found");

        // Act
        await _lrcService.GetLyricsAsync(song);

        // Assert
        // Verify it was called with "Primary Artist", NOT "Primary Artist & Secondary Artist"
        await _onlineLyricsService.Received(1).GetLyricsAsync(
            Arg.Is("Multi Artist Track"),
            Arg.Is("Primary Artist"),
            Arg.Any<string?>(),
            Arg.Any<TimeSpan>(),
            Arg.Any<CancellationToken>());
        
        await _netEaseLyricsService.Received(1).SearchLyricsAsync(
            Arg.Is("Multi Artist Track"),
            Arg.Is("Primary Artist"),
            Arg.Any<CancellationToken>());
    }

    #endregion
}