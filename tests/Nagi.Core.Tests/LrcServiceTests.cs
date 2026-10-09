using FluentAssertions;
using Microsoft.Extensions.Logging;
using System.Net;
using Nagi.Core.Helpers;
using Nagi.Core.Models;
using Nagi.Core.Models.Lyrics;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Implementations;
using Nagi.Core.Tests.Utils;
using NSubstitute;
using Xunit;

namespace Nagi.Core.Tests;

public class LrcServiceTests : IDisposable
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

    public void Dispose() => _lrcService.Dispose();

    [Fact]
    public async Task LrcService_RespectsProviderOrder()
    {
        var song = new Song { Title = "Test Song", Duration = TimeSpan.FromMinutes(3) };
        _settingsService.GetFetchOnlineLyricsEnabledAsync().Returns(true);

        _settingsService.GetEnabledServiceProvidersAsync(ServiceCategory.Lyrics)
            .Returns(new List<ServiceProviderSetting>
            {
                new() { Id = ServiceProviderIds.NetEase, Order = 0, IsEnabled = true },
                new() { Id = ServiceProviderIds.LrcLib, Order = 1, IsEnabled = true }
            });

        _netEaseLyricsService.SearchLyricsAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns("[00:01.00]From NetEase");
        _onlineLyricsService.GetLyricsAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns("[00:01.00]From LRCLIB");

        var result = await _lrcService.GetLyricsAsync(song);

        result.Should().NotBeNull();
        result!.Lines[0].Text.Should().Be("From NetEase");
    }

    [Theory]
    [InlineData("First plain", "[00:01.00]Second synced", "[00:01.00]Second synced", true)]
    [InlineData("First plain", "Second plain", "First plain", false)]
    [InlineData(null, "Second plain", "Second plain", false)]
    public async Task GetLyricsAsync_PrefersTimedLyricsThenProviderOrderAndCachesTheSelectedText(
        string? first, string? second, string expected, bool timed)
    {
        var song = new Song { Title = "Song", FilePath = @"C:\Music\song.mp3" };
        _settingsService.GetFetchOnlineLyricsEnabledAsync().Returns(true);
        _pathConfig.LrcCachePath.Returns(@"C:\LyricsCache");
        _fileSystem.Combine(Arg.Any<string[]>())
            .Returns(call => Path.Combine(call.Arg<string[]>()));
        _onlineLyricsService.GetLyricsAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(first);
        _netEaseLyricsService.SearchLyricsAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(second);

        var result = await _lrcService.GetLyricsAsync(song);

        result!.IsEmpty.Should().Be(!timed);
        result.RawUnsyncedLyrics.Should().Be(expected);
        await _fileSystem.Received(1).WriteAllTextAsync(Arg.Any<string>(), expected);
        _fileSystem.FileExists(song.LrcFilePath!).Returns(true);
        _fileSystem.ReadAllTextAsync(song.LrcFilePath!).Returns(expected);
        _onlineLyricsService.ClearReceivedCalls();
        _netEaseLyricsService.ClearReceivedCalls();

        var cached = await _lrcService.GetLyricsAsync(song);

        cached!.IsEmpty.Should().Be(!timed);
        cached.RawUnsyncedLyrics.Should().Be(expected);
        await _onlineLyricsService.DidNotReceive().GetLyricsAsync(Arg.Any<string>(), Arg.Any<string?>(),
            Arg.Any<string?>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
        await _netEaseLyricsService.DidNotReceive().SearchLyricsAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetLyricsAsync_WhenOneProviderFails_DoesNotCacheNegativeResult(bool timeout)
    {
        var song = new Song { Id = Guid.NewGuid(), Title = "Retryable" };
        _settingsService.GetFetchOnlineLyricsEnabledAsync().Returns(true);
        Exception failure = timeout ? new TaskCanceledException("Provider timeout") : new HttpRequestException("Offline");
        _onlineLyricsService.GetLyricsAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(),
                Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<string?>(failure), Task.FromResult<string?>("[00:01.00]Recovered"));

        (await _lrcService.GetLyricsAsync(song)).Should().BeNull();
        song.LyricsLastCheckedUtc.Should().BeNull();
        await _libraryWriter.DidNotReceive().UpdateSongLyricsLastCheckedAsync(song.Id);

        var retry = await _lrcService.GetLyricsAsync(song);
        retry.Should().NotBeNull();
        retry!.Lines.Should().ContainSingle(line => line.Text == "Recovered");
    }

    [Fact]
    public async Task GetLyricsAsync_WhenFirstProviderTimesOut_UsesFallbackLyrics()
    {
        _settingsService.GetFetchOnlineLyricsEnabledAsync().Returns(true);
        _onlineLyricsService.GetLyricsAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(),
                Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<string?>(new TaskCanceledException("Provider timeout")));
        _netEaseLyricsService.SearchLyricsAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns("[00:01.00]Fallback");

        var result = await _lrcService.GetLyricsAsync(new Song { Title = "Song" });

        result.Should().NotBeNull();
        result!.Lines.Should().ContainSingle(line => line.Text == "Fallback");
    }

    [Fact]
    public async Task GetLyricsAsync_WhenCancelledBeforeProviderCompletes_DoesNotCacheLyrics()
    {
        using var cts = new CancellationTokenSource();
        var song = new Song { Id = Guid.NewGuid(), Title = "Cancelled" };
        _settingsService.GetFetchOnlineLyricsEnabledAsync().Returns(true);
        _onlineLyricsService.GetLyricsAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(),
                Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                cts.Cancel();
                return Task.FromResult<string?>("[00:01.00]Too late");
            });

        var result = await _lrcService.GetLyricsAsync(song, cts.Token);

        result.Should().BeNull();
        await _libraryWriter.DidNotReceive().UpdateSongLyricsLastCheckedAsync(song.Id);
        await _libraryWriter.DidNotReceive().UpdateSongLrcPathAsync(song.Id, Arg.Any<string?>());
    }

    [Theory]
    [InlineData(ServiceProviderIds.LrcLib)]
    [InlineData(ServiceProviderIds.NetEase)]
    public async Task GetLyricsAsync_AfterProviderOutage_RetriesAndFindsLyrics(string providerId)
    {
        using var handler = new TestHttpMessageHandler();
        using var client = new HttpClient(handler);
        await using var pipeline = TestProviderPipeline.Build(providerId);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(client);
        var onlineService = new LrcLibService(factory, pipeline, Substitute.For<ILogger<LrcLibService>>());
        var netEaseService = new NetEaseLyricsService(factory, pipeline, Substitute.For<ILogger<NetEaseLyricsService>>());
        using var service = new LrcService(_fileSystem, onlineService, netEaseService, _settingsService,
            _pathConfig, _libraryWriter, _logger);
        var song = new Song { Id = Guid.NewGuid(), Title = "Song", DurationTicks = TimeSpan.FromSeconds(180).Ticks };
        _settingsService.GetFetchOnlineLyricsEnabledAsync().Returns(true);
        _settingsService.GetEnabledServiceProvidersAsync(ServiceCategory.Lyrics).Returns(
            new List<ServiceProviderSetting> { new() { Id = providerId, IsEnabled = true } });
        handler.SendAsyncFunc = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        (await service.GetLyricsAsync(song)).Should().BeNull();
        song.LyricsLastCheckedUtc.Should().BeNull();
        await _libraryWriter.DidNotReceive().UpdateSongLyricsLastCheckedAsync(song.Id);

        handler.SendAsyncFunc = (request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(providerId == ServiceProviderIds.LrcLib
                ? "[{\"syncedLyrics\":\"[00:01.00]Recovered\",\"duration\":180}]"
                : request.RequestUri!.AbsolutePath.Contains("search")
                    ? "{\"result\":{\"songs\":[{\"id\":123,\"name\":\"Song\"}]}}"
                    : "{\"lrc\":{\"lyric\":\"[00:01.00]Recovered\"}}")
        });

        var result = await service.GetLyricsAsync(song);

        result.Should().NotBeNull();
        result!.Lines.Should().ContainSingle(line => line.Text == "Recovered");
    }

    [Fact]
    public async Task GetLyricsAsync_WithValidLrcFile_ReturnsParsedAndOrderedLyrics()
    {
        var lrcContent = "[00:05.00]Line 2\n[00:01.00]Line 1";
        _fileSystem.FileExists(LrcPath).Returns(true);
        _fileSystem.ReadAllTextAsync(LrcPath).Returns(lrcContent);

        var result = await _lrcService.GetLyricsAsync(LrcPath);

        result.Should().NotBeNull();
        result!.IsEmpty.Should().BeFalse();
        result.Lines.Should().HaveCount(2);
        result.Lines[0].Text.Should().Be("Line 1");
        result.Lines[0].StartTime.Should().Be(TimeSpan.FromSeconds(1));
        result.Lines[1].Text.Should().Be("Line 2");
        result.Lines[1].StartTime.Should().Be(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task GetLyricsAsync_WithNonExistentFile_ReturnsNull()
    {
        _fileSystem.FileExists(LrcPath).Returns(false);

        var result = await _lrcService.GetLyricsAsync(LrcPath);

        result.Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GetLyricsAsync_WithInvalidPath_ReturnsNull(string? path)
    {
        var result = await _lrcService.GetLyricsAsync(path!);

        result.Should().BeNull();
    }

    [Fact]
    public void ParseLyrics_WithLanguagePrefix_StripsPrefix()
    {
        var content = "eng||[00:01.00]Line 1\n[00:02.00]Line 2";

        var result = _lrcService.ParseLyrics(content);

        result.Should().NotBeNull();
        result.Lines.Should().HaveCount(2);
        result.Lines[0].Text.Should().Be("Line 1");
        result.Lines[0].StartTime.Should().Be(TimeSpan.FromSeconds(1));
        result.Lines[1].Text.Should().Be("Line 2");
        result.Lines[1].StartTime.Should().Be(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task GetLyricsAsync_WithEmptyFileContent_ReturnsEmptyParsedLrc()
    {
        _fileSystem.FileExists(LrcPath).Returns(true);
        _fileSystem.ReadAllTextAsync(LrcPath).Returns(string.Empty);

        var result = await _lrcService.GetLyricsAsync(LrcPath);

        result.Should().NotBeNull();
        result!.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public async Task RemoveCachedLyricsAsync_DeletesOnlyManagedFileAndPreventsRedownload()
    {
        const string cacheRoot = @"C:\cache\lrc";
        var cachedPath = Path.Combine(cacheRoot, "song.lrc");
        var song = new Song { Id = Guid.NewGuid(), LrcFilePath = cachedPath };
        _pathConfig.LrcCachePath.Returns(cacheRoot);
        _fileSystem.FileExists(cachedPath).Returns(true);
        _settingsService.GetFetchOnlineLyricsEnabledAsync().Returns(true);

        var removed = await _lrcService.RemoveCachedLyricsAsync(song);
        var result = await _lrcService.GetLyricsAsync(song);

        removed.Should().BeTrue();
        result.Should().BeNull();
        _fileSystem.Received(1).DeleteFile(cachedPath);
        await _libraryWriter.Received(1).UpdateSongLrcPathAsync(song.Id, null);
        await _libraryWriter.Received(1).UpdateSongLyricsLastCheckedAsync(song.Id);
        await _onlineLyricsService.DidNotReceive().GetLyricsAsync(
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
        song.LrcFilePath.Should().BeNull();
        song.LyricsLastCheckedUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task SaveAndRemoveLyrics_WithExternalSidecar_RestoresSidecar()
    {
        const string cacheRoot = @"C:\cache\lrc";
        const string externalPath = @"C:\Music\Song.lrc";
        const string content = "[00:01.00]Correct lyrics";
        var song = new Song
        {
            Id = Guid.NewGuid(),
            FilePath = @"C:\Music\Song.flac",
            Title = "Song",
            PrimaryArtistName = "Artist",
            LrcFilePath = externalPath,
            Album = new Album { Title = "Album" }
        };
        var expectedPath = Path.Combine(cacheRoot, FileNameHelper.GenerateLrcOverrideFileName(song.FilePath));
        _pathConfig.LrcCachePath.Returns(cacheRoot);
        _fileSystem.Combine(Arg.Any<string[]>()).Returns(call => Path.Combine(call.Arg<string[]>()!));
        _fileSystem.GetDirectoryName(song.FilePath).Returns(@"C:\Music");
        _fileSystem.GetFileNameWithoutExtension(song.FilePath).Returns("Song");
        _fileSystem.GetFileNameWithoutExtension(externalPath).Returns("Song");
        _fileSystem.GetFiles(@"C:\Music", "*.lrc").Returns([externalPath]);
        _fileSystem.FileExists(expectedPath).Returns(true);

        await _lrcService.SaveLyricsAsync(song, content);
        var removed = await _lrcService.RemoveCachedLyricsAsync(song);

        removed.Should().BeTrue();
        await _fileSystem.Received(1).WriteAllTextAsync(
            Arg.Is<string>(path => path.StartsWith(expectedPath + ".") && path.EndsWith(".tmp")), content);
        _fileSystem.Received(1).MoveFile(Arg.Any<string>(), expectedPath, true);
        await _fileSystem.DidNotReceive().WriteAllTextAsync(externalPath, Arg.Any<string>());
        _fileSystem.Received(1).DeleteFile(expectedPath);
        _fileSystem.DidNotReceive().DeleteFile(externalPath);
        await _libraryWriter.Received(1).UpdateSongLrcPathAsync(song.Id, expectedPath);
        await _libraryWriter.Received(1).UpdateSongLrcPathAsync(song.Id, externalPath);
        song.LrcFilePath.Should().Be(externalPath);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SaveLyricsAsync_WhenWriteOrReplaceFails_PreservesExistingLyrics(bool failOnReplace)
    {
        const string cacheRoot = @"C:\cache\lrc";
        var song = new Song { FilePath = @"C:\Music\Song.flac" };
        var previousPath = Path.Combine(cacheRoot, FileNameHelper.GenerateLrcOverrideFileName(song.FilePath));
        song.LrcFilePath = previousPath;
        _pathConfig.LrcCachePath.Returns(cacheRoot);
        _fileSystem.Combine(Arg.Any<string[]>()).Returns(call => Path.Combine(call.Arg<string[]>()));
        _fileSystem.FileExists(Arg.Any<string>()).Returns(true);
        string? temporaryPath = null;
        _fileSystem.WriteAllTextAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(call =>
        {
            temporaryPath = call.ArgAt<string>(0);
            return failOnReplace ? Task.CompletedTask : Task.FromException(new IOException("Write failed"));
        });
        _fileSystem.When(fs => fs.MoveFile(Arg.Any<string>(), previousPath, true))
            .Do(_ => throw new IOException("Replace failed"));

        var save = () => _lrcService.SaveLyricsAsync(song, "[00:01.00]New lyrics");
        await save.Should().ThrowAsync<IOException>();

        temporaryPath.Should().StartWith(previousPath + ".").And.EndWith(".tmp");
        _fileSystem.Received(1).DeleteFile(temporaryPath!);
        _fileSystem.DidNotReceive().DeleteFile(previousPath);
        await _fileSystem.DidNotReceive().WriteAllTextAsync(previousPath, Arg.Any<string>());
        await _libraryWriter.DidNotReceive().UpdateSongLrcPathAsync(Arg.Any<Guid>(), Arg.Any<string?>());
        song.LrcFilePath.Should().Be(previousPath);
    }

    [Theory]
    [InlineData(@"C:\Music\Song.lrc")]
    [InlineData(@"C:\cache\lrc\..\Song.lrc")]
    [InlineData(@"C:\cache\lrc-other\Song.lrc")]
    public async Task RemoveCachedLyricsAsync_WithExternalSidecar_DoesNotDeleteFile(string path)
    {
        var song = new Song { Id = Guid.NewGuid(), LrcFilePath = path };
        _pathConfig.LrcCachePath.Returns(@"C:\cache\lrc");

        var removed = await _lrcService.RemoveCachedLyricsAsync(song);

        removed.Should().BeFalse();
        _fileSystem.DidNotReceive().DeleteFile(Arg.Any<string>());
        await _libraryWriter.DidNotReceive().UpdateSongLrcPathAsync(Arg.Any<Guid>(), Arg.Any<string?>());
    }

    [Fact]
    public void GetCurrentLine_WithEmptyParsedLrc_ReturnsNull()
    {
        var empty = new ParsedLrc(Enumerable.Empty<LyricLine>());
        var hint = 0;
        var result = _lrcService.GetCurrentLine(empty, TimeSpan.FromSeconds(5), ref hint);
        result.Should().BeNull();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void GetCurrentLine_WithDuplicateTimestamps_SelectsLastMatchingLine(int hint)
    {
        var parsed = _lrcService.ParseLyrics("[00:01.00]First\n[00:01.00]Second\n[00:02.00]Third");

        var line = _lrcService.GetCurrentLine(parsed, TimeSpan.FromSeconds(1), ref hint);

        line!.Text.Should().Be("Second");
        hint.Should().Be(1);
    }

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

    [Theory]
    [InlineData(5, null)]
    [InlineData(10, "Line 1")]
    [InlineData(15, "Line 1")]
    [InlineData(29.9, "Line 2")]
    [InlineData(30, "Line 3")]
    [InlineData(100, "Line 3")]
    public void GetCurrentLine_Simple_ReturnsCorrectLineForTime(double currentTimeSec, string? expectedText)
    {
        var parsedLrc = CreateTestParsedLrc();
        var currentTime = TimeSpan.FromSeconds(currentTimeSec);

        var hint = 0;
        var result = _lrcService.GetCurrentLine(parsedLrc, currentTime, ref hint);

        result?.Text.Should().Be(expectedText);
    }

    [Fact]
    public void GetCurrentLine_WithHint_WhenHintIsCorrect_ReturnsLineWithoutSearching()
    {
        var parsedLrc = CreateTestParsedLrc();
        var searchIndex = 1;
        var result = _lrcService.GetCurrentLine(parsedLrc, TimeSpan.FromSeconds(25), ref searchIndex);

        result?.Text.Should().Be("Line 2");
        searchIndex.Should().Be(1);
    }

    [Fact]
    public void GetCurrentLine_WithHint_WhenSeekingForward_FindsCorrectLineAndUpdatesHint()
    {
        var parsedLrc = CreateTestParsedLrc();
        var searchIndex = 0;
        var result = _lrcService.GetCurrentLine(parsedLrc, TimeSpan.FromSeconds(35), ref searchIndex);

        result?.Text.Should().Be("Line 3");
        searchIndex.Should().Be(2);
    }

    [Fact]
    public void GetCurrentLine_WithHint_WhenSeekingBackward_FindsCorrectLineAndUpdatesHint()
    {
        var parsedLrc = CreateTestParsedLrc();
        var searchIndex = 2;
        var result = _lrcService.GetCurrentLine(parsedLrc, TimeSpan.FromSeconds(12), ref searchIndex);

        result?.Text.Should().Be("Line 1");
        searchIndex.Should().Be(0);
    }

    [Fact]
    public async Task GetLyricsAsync_WithNoLocalFile_FetchesBothInParallel_PrefersLrcLib()
    {
        var song = new Song { Title = "Test", LrcFilePath = null, Duration = TimeSpan.FromMinutes(3) };
        var preferredResult = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _settingsService.GetFetchOnlineLyricsEnabledAsync().Returns(true);
        _onlineLyricsService.GetLyricsAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(preferredResult.Task);
        _netEaseLyricsService.SearchLyricsAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                preferredResult.SetResult("[00:01.00]From LRCLIB");
                return Task.FromResult<string?>("[00:01.00]From NetEase");
            });

        var result = await _lrcService.GetLyricsAsync(song).WaitAsync(TimeSpan.FromSeconds(5));
        result.Should().NotBeNull();
        result!.Lines.Should().HaveCount(1);
        result.Lines[0].Text.Should().Be("From LRCLIB");
        await _onlineLyricsService.Received(1).GetLyricsAsync(Arg.Any<string>(), Arg.Any<string?>(),
            Arg.Any<string?>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
        await _netEaseLyricsService.Received(1).SearchLyricsAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetLyricsAsync_WithLegacyCachePath_InvalidatesAndRefreshesCollisionProneFile()
    {
        const string cacheRoot = @"C:\cache\lrc";
        const string legacyPath = @"C:\cache\lrc\Artist - Album - Song.lrc";
        var song = new Song
        {
            Title = "Song",
            PrimaryArtistName = "Artist",
            FilePath = @"C:\Music\Song.flac",
            LrcFilePath = legacyPath,
            LyricsLastCheckedUtc = DateTime.UtcNow,
            Duration = TimeSpan.FromMinutes(3),
            Album = new Album { Title = "Album" }
        };
        _pathConfig.LrcCachePath.Returns(cacheRoot);
        _fileSystem.Combine(Arg.Any<string[]>())
            .Returns(call => Path.Combine(call.Arg<string[]>()!));
        _fileSystem.FileExists(legacyPath).Returns(true);
        _settingsService.GetFetchOnlineLyricsEnabledAsync().Returns(true);
        _onlineLyricsService.GetLyricsAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(),
                Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns("[00:01.00]Fresh Lyrics");
        _netEaseLyricsService.SearchLyricsAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);

        var result = await _lrcService.GetLyricsAsync(song);

        result!.Lines.Single().Text.Should().Be("Fresh Lyrics");
        await _fileSystem.DidNotReceive().ReadAllTextAsync(legacyPath);
        await _libraryWriter.Received(1).UpdateSongLrcPathAsync(
            song.Id,
            Arg.Is<string?>(path => path != null && path != legacyPath && path.EndsWith(".lrc")));
    }

    [Fact]
    public async Task GetLyricsAsync_WithIdentityBasedCachePath_UsesLocalFile()
    {
        const string cacheRoot = @"C:\cache\lrc";
        var song = new Song
        {
            Title = "Song",
            PrimaryArtistName = "Artist",
            FilePath = @"C:\Music\Song.flac",
            Album = new Album { Title = "Album" }
        };
        var cachePath = Path.Combine(cacheRoot, FileNameHelper.GenerateLrcCacheFileName(
            song.FilePath, song.PrimaryArtistName, song.Album.Title, song.Title));
        song.LrcFilePath = cachePath;
        _pathConfig.LrcCachePath.Returns(cacheRoot);
        _fileSystem.Combine(Arg.Any<string[]>())
            .Returns(call => Path.Combine(call.Arg<string[]>()!));
        _fileSystem.FileExists(cachePath).Returns(true);
        _fileSystem.ReadAllTextAsync(cachePath).Returns("[00:01.00]Local Lyrics");

        var result = await _lrcService.GetLyricsAsync(song);

        result!.Lines.Single().Text.Should().Be("Local Lyrics");
        await _libraryWriter.DidNotReceive().UpdateSongLrcPathAsync(Arg.Any<Guid>(), Arg.Any<string?>());
        await _onlineLyricsService.DidNotReceive().GetLyricsAsync(
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetLyricsAsync_WithIdentityBasedCachePathAfterMetadataEdit_UsesLocalFile()
    {
        const string cacheRoot = @"C:\cache\lrc";
        const string audioFilePath = @"C:\Music\Song.flac";
        var cachePath = Path.Combine(cacheRoot, FileNameHelper.GenerateLrcCacheFileName(
            audioFilePath, "Original Artist", "Original Album", "Original Title"));
        var song = new Song
        {
            Title = "Corrected Title",
            PrimaryArtistName = "Corrected Artist",
            FilePath = audioFilePath,
            LrcFilePath = cachePath,
            LyricsLastCheckedUtc = DateTime.UtcNow,
            Album = new Album { Title = "Corrected Album" }
        };
        _pathConfig.LrcCachePath.Returns(cacheRoot);
        _fileSystem.FileExists(cachePath).Returns(true);
        _fileSystem.ReadAllTextAsync(cachePath).Returns("[00:01.00]Cached Lyrics");

        var result = await _lrcService.GetLyricsAsync(song);

        result!.Lines.Single().Text.Should().Be("Cached Lyrics");
        await _settingsService.DidNotReceive().GetFetchOnlineLyricsEnabledAsync();
        await _onlineLyricsService.DidNotReceive().GetLyricsAsync(
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetLyricsAsync_WhenLrcLibReturnsNull_UsesNetEaseResult()
    {
        var song = new Song { Title = "Japanese Song", LrcFilePath = null, Duration = TimeSpan.FromMinutes(3) };
        _settingsService.GetFetchOnlineLyricsEnabledAsync().Returns(true);
        _onlineLyricsService.GetLyricsAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns((string?)null);
        _netEaseLyricsService.SearchLyricsAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns("[00:01.00]From NetEase");

        var result = await _lrcService.GetLyricsAsync(song);
        result.Should().NotBeNull();
        result!.Lines[0].Text.Should().Be("From NetEase");
    }

    [Fact]
    public async Task GetLyricsAsync_WhenOnlineFetchDisabled_DoesNotCallRemoteServices()
    {
        var song = new Song { Title = "Test", LrcFilePath = null };
        _settingsService.GetFetchOnlineLyricsEnabledAsync().Returns(false);

        var result = await _lrcService.GetLyricsAsync(song);

        result.Should().BeNull();
        await _onlineLyricsService.DidNotReceive().GetLyricsAsync(Arg.Any<string>(), Arg.Any<string?>(),
            Arg.Any<string?>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
        await _netEaseLyricsService.DidNotReceive().SearchLyricsAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetLyricsAsync_WhenNoProvidersEnabled_DoesNotFetchOrCacheNegativeResult()
    {
        var song = new Song { Title = "Test", LrcFilePath = null, Duration = TimeSpan.FromMinutes(3) };
        _settingsService.GetFetchOnlineLyricsEnabledAsync().Returns(true);
        _settingsService.GetEnabledServiceProvidersAsync(ServiceCategory.Lyrics)
            .Returns(new List<ServiceProviderSetting>());

        var result = await _lrcService.GetLyricsAsync(song);

        result.Should().BeNull();
        await _onlineLyricsService.DidNotReceive().GetLyricsAsync(Arg.Any<string>(), Arg.Any<string?>(),
            Arg.Any<string?>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
        await _netEaseLyricsService.DidNotReceive().SearchLyricsAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await _libraryWriter.DidNotReceive().UpdateSongLyricsLastCheckedAsync(Arg.Any<Guid>());
        song.LyricsLastCheckedUtc.Should().BeNull();
    }

    [Theory]
    [InlineData(ServiceProviderIds.LrcLib, "From LRCLIB")]
    [InlineData(ServiceProviderIds.NetEase, "From NetEase")]
    public async Task GetLyricsAsync_WithOneEnabledProvider_OnlyCallsThatProvider(string providerId, string expectedText)
    {
        var song = new Song { Title = "Test", Duration = TimeSpan.FromMinutes(3) };
        _settingsService.GetFetchOnlineLyricsEnabledAsync().Returns(true);
        _settingsService.GetEnabledServiceProvidersAsync(ServiceCategory.Lyrics).Returns(
            new List<ServiceProviderSetting> { new() { Id = providerId, IsEnabled = true } });
        _onlineLyricsService.GetLyricsAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(),
                Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns("[00:01.00]From LRCLIB");
        _netEaseLyricsService.SearchLyricsAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns("[00:01.00]From NetEase");

        var result = await _lrcService.GetLyricsAsync(song);

        result!.Lines[0].Text.Should().Be(expectedText);
        await _onlineLyricsService.Received(providerId == ServiceProviderIds.LrcLib ? 1 : 0)
            .GetLyricsAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
        await _netEaseLyricsService.Received(providerId == ServiceProviderIds.NetEase ? 1 : 0)
            .SearchLyricsAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ParseLyrics_WithNullOrWhitespace_ReturnsEmptyParsedLrc(string? content)
    {
        var result = _lrcService.ParseLyrics(content);

        result.Should().NotBeNull();
        result.IsEmpty.Should().BeTrue();
        result.Lines.Should().BeEmpty();
    }

    [Fact]
    public void ParseLyrics_WithThreeDigitMilliseconds_PreservesPrecision()
    {
        var content = "[00:01.123]First Line\n[00:05.456]Second Line";

        var result = _lrcService.ParseLyrics(content);

        result.IsEmpty.Should().BeFalse();
        result.Lines.Should().HaveCount(2);
        result.Lines[0].Text.Should().Be("First Line");
        result.Lines[0].StartTime.Should().Be(TimeSpan.FromMilliseconds(1123));
        result.Lines[1].StartTime.Should().Be(TimeSpan.FromMilliseconds(5456));
    }

    [Fact]
    public void ParseLyrics_WithRepeatedTimestampsAndOffset_ExpandsAndAppliesOffset()
    {
        var content = "[offset:250]\n[00:01.00][00:02.00]Same Line";

        var result = _lrcService.ParseLyrics(content);

        result.Lines.Should().HaveCount(2);
        result.Lines.Select(line => line.Text).Should().Equal("Same Line", "Same Line");
        result.Lines.Select(line => line.StartTime).Should().Equal(
            TimeSpan.FromMilliseconds(1250),
            TimeSpan.FromMilliseconds(2250));
    }

    [Fact]
    public async Task GetLyricsAsync_WhenLyricsAlreadyChecked_ReturnsNullWithoutFetching()
    {
        var song = new Song { Title = "Test", LrcFilePath = null, LyricsLastCheckedUtc = DateTime.UtcNow };
        _settingsService.GetFetchOnlineLyricsEnabledAsync().Returns(true);

        var result = await _lrcService.GetLyricsAsync(song);

        result.Should().BeNull();
        await _onlineLyricsService.DidNotReceive().GetLyricsAsync(Arg.Any<string>(), Arg.Any<string?>(),
            Arg.Any<string?>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetLyricsAsync_WhenAlreadyCancelled_DoesNotCallOnlineServices()
    {
        var song = new Song { Title = "Test", LrcFilePath = null, Duration = TimeSpan.FromMinutes(3) };
        _settingsService.GetFetchOnlineLyricsEnabledAsync().Returns(true);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await _lrcService.GetLyricsAsync(song, cts.Token);
        result.Should().BeNull();
        await _onlineLyricsService.DidNotReceive().GetLyricsAsync(Arg.Any<string>(), Arg.Any<string?>(),
            Arg.Any<string?>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
        await _netEaseLyricsService.DidNotReceive().SearchLyricsAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetLyricsAsync_WithLocalFile_DoesNotFetchOnline()
    {
        var song = new Song { Title = "Test", LrcFilePath = LrcPath };
        _fileSystem.FileExists(LrcPath).Returns(true);
        _fileSystem.ReadAllTextAsync(LrcPath).Returns("[00:01.00]Local Lyrics");
        _settingsService.GetFetchOnlineLyricsEnabledAsync().Returns(true);

        var result = await _lrcService.GetLyricsAsync(song);

        result.Should().NotBeNull();
        result!.Lines[0].Text.Should().Be("Local Lyrics");
        await _onlineLyricsService.DidNotReceive().GetLyricsAsync(Arg.Any<string>(), Arg.Any<string?>(),
            Arg.Any<string?>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetLyricsAsync_WithMultiArtistSong_CallsProvidersWithPrimaryArtist()
    {
        var song = new Song { Title = "Multi Artist Track", Duration = TimeSpan.FromMinutes(3) };
        var artist1 = new Artist { Name = "Primary Artist" };
        var artist2 = new Artist { Name = "Secondary Artist" };
        song.SongArtists.Add(new SongArtist { Song = song, Artist = artist1, Order = 0 });
        song.SongArtists.Add(new SongArtist { Song = song, Artist = artist2, Order = 1 });
        song.SyncDenormalizedFields();

        _settingsService.GetFetchOnlineLyricsEnabledAsync().Returns(true);
        _onlineLyricsService.GetLyricsAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns("[00:01.00]Found");

        await _lrcService.GetLyricsAsync(song);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetLyricsAsync_WhenFetchDisabled_StopsWaitingForProvider(bool plainFound)
    {
        var song = new Song { Title = "Song" };
        _settingsService.GetFetchOnlineLyricsEnabledAsync().Returns(true);
        var pending = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken providerToken = default;
        Task<string?> Hold(CancellationToken token)
        {
            providerToken = token;
            return pending.Task;
        }
        if (plainFound)
        {
            _onlineLyricsService.GetLyricsAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(),
                Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns("Plain lyrics");
            _netEaseLyricsService.SearchLyricsAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(call => Hold(call.Arg<CancellationToken>()));
        }
        else
        {
            _onlineLyricsService.GetLyricsAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(),
                Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
                .Returns(call => Hold(call.Arg<CancellationToken>()));
        }

        var fetch = _lrcService.GetLyricsAsync(song);
        _settingsService.FetchOnlineLyricsEnabledChanged += Raise.Event<Action<bool>>(false);
        try
        {
            var result = await fetch.WaitAsync(TimeSpan.FromSeconds(5));

            providerToken.IsCancellationRequested.Should().BeTrue();
            result.Should().BeNull();
            await _libraryWriter.DidNotReceive().UpdateSongLyricsLastCheckedAsync(song.Id);
            await _fileSystem.DidNotReceive().WriteAllTextAsync(Arg.Any<string>(), Arg.Any<string>());
        }
        finally
        {
            pending.TrySetCanceled(providerToken);
        }
    }

    [Fact]
    public async Task GetLyricsAsync_WhenFetchReEnabledAfterDisable_AllowsNewFetch()
    {
        var song = new Song { Title = "Song" };
        _settingsService.GetFetchOnlineLyricsEnabledAsync().Returns(true);
        _onlineLyricsService.GetLyricsAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns("[00:01.00]Lyric");

        _settingsService.FetchOnlineLyricsEnabledChanged += Raise.Event<Action<bool>>(false);
        _settingsService.FetchOnlineLyricsEnabledChanged += Raise.Event<Action<bool>>(true);
        var result = await _lrcService.GetLyricsAsync(song);

        result.Should().NotBeNull();
        await _onlineLyricsService.Received(1).GetLyricsAsync(
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetLyricsAsync_WhenProvidersReturnEmpty_MarksLyricsAsChecked()
    {
        var song = new Song { Title = "Unknown Song" };
        _settingsService.GetFetchOnlineLyricsEnabledAsync().Returns(true);
        _onlineLyricsService.GetLyricsAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns((string?)null);
        _netEaseLyricsService.SearchLyricsAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);

        var result = await _lrcService.GetLyricsAsync(song);
        result.Should().BeNull();
        await _libraryWriter.Received(1).UpdateSongLyricsLastCheckedAsync(song.Id);
    }
}
