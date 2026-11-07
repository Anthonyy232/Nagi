using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Net;
using Nagi.Core.Helpers;
using Nagi.Core.Library;
using Nagi.Core.Lyrics;
using Nagi.Core.Models;
using Nagi.Core.MusicServers;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;
using Nagi.Core.Services.Implementations;
using Nagi.Core.Settings;
using Nagi.Core.Tests.Utils;
using NSubstitute;
using Xunit;

namespace Nagi.Core.Tests;

public class LrcServiceTests : IDisposable
{
    private const string LrcPath = "C:\\lyrics\\test.lrc";
    private readonly IFileSystemService _fileSystem;
    private readonly ILyricsProvider _lrcLib = LyricsProvider(ServiceProviderIds.LrcLib);
    private readonly SettingsService _settingsService;
    private readonly IPathConfiguration _pathConfig;
    private readonly DbContextFactoryTestHelper _db = new();
    private readonly ILogger<LrcService> _logger;
    private readonly ILyricsProvider _netEase = LyricsProvider(ServiceProviderIds.NetEase);
    private readonly IMusicServerService _musicServers = Substitute.For<IMusicServerService>();
    private readonly LrcService _lrcService;

    public LrcServiceTests()
    {
        _fileSystem = Substitute.For<IFileSystemService>();
        _settingsService = TestSettings.Create();
        _pathConfig = Substitute.For<IPathConfiguration>();
        _logger = Substitute.For<ILogger<LrcService>>();

        _lrcService = new LrcService(
            _fileSystem,
            [_lrcLib, _netEase],
            _settingsService,
            _pathConfig,
            _db.ContextFactory,
            _musicServers,
            _logger);
    }

    public void Dispose()
    {
        _lrcService.Dispose();
        _db.Dispose();
    }

    private async Task<Song> SeedAsync(Song song)
    {
        await using var context = _db.ContextFactory.CreateDbContext();
        song.Folder = new Folder { Name = "Music", Path = Path.Combine("C:", "Music") };
        if (string.IsNullOrEmpty(song.FilePath)) song.FilePath = Path.Combine(song.Folder.Path, $"{song.Id}.mp3");
        context.Songs.Add(song);
        await context.SaveChangesAsync();
        return song;
    }

    private async Task<Song> StoredAsync(Song song)
    {
        await using var context = _db.ContextFactory.CreateDbContext();
        return await context.Songs.AsNoTracking().SingleAsync(s => s.Id == song.Id);
    }

    [Fact]
    public async Task LrcService_RespectsProviderOrder()
    {
        var song = new Song { Title = "Test Song", Duration = TimeSpan.FromMinutes(3) };
        _settingsService.Set(CoreSettings.FetchOnlineLyrics, true);

        _settingsService.EnableOnly(CoreSettings.LyricsProviders, ServiceProviderIds.NetEase, ServiceProviderIds.LrcLib);

        _netEase.GetLyricsAsync(Arg.Any<Song>(), Arg.Any<CancellationToken>())
            .Returns(Found("[00:01.00]From NetEase"));
        _lrcLib.GetLyricsAsync(Arg.Any<Song>(), Arg.Any<CancellationToken>())
            .Returns(Found("[00:01.00]From LRCLIB"));

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
        _settingsService.Set(CoreSettings.FetchOnlineLyrics, true);
        _pathConfig.LrcCachePath.Returns(@"C:\LyricsCache");
        _lrcLib.GetLyricsAsync(Arg.Any<Song>(), Arg.Any<CancellationToken>()).Returns(Found(first));
        _netEase.GetLyricsAsync(Arg.Any<Song>(), Arg.Any<CancellationToken>())
            .Returns(Found(second));

        var result = await _lrcService.GetLyricsAsync(song);

        result!.IsSynced.Should().Be(timed);
        result.RawLyrics.Should().Be(expected);
        await _fileSystem.Received(1).WriteAllTextAsync(Arg.Any<string>(), expected);
        _fileSystem.FileExists(song.LrcFilePath!).Returns(true);
        _fileSystem.ReadAllTextAsync(song.LrcFilePath!).Returns(expected);
        _lrcLib.ClearReceivedCalls();
        _netEase.ClearReceivedCalls();

        var cached = await _lrcService.GetLyricsAsync(song);

        cached!.IsSynced.Should().Be(timed);
        cached.RawLyrics.Should().Be(expected);
        await _lrcLib.DidNotReceive().GetLyricsAsync(Arg.Any<Song>(), Arg.Any<CancellationToken>());
        await _netEase.DidNotReceive().GetLyricsAsync(Arg.Any<Song>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetLyricsAsync_ServerSong_UsesAndCachesTheServersLyricsWithOnlineLyricsOff()
    {
        var song = new Song { Title = "Breakwater", FilePath = @"nagi-server:0123\s1", RemoteId = "s1" };
        _pathConfig.LrcCachePath.Returns(@"C:\LyricsCache");
        _musicServers.GetLyricsAsync(song, Arg.Any<CancellationToken>()).Returns("[00:01.000]Salt on the window");

        var result = await _lrcService.GetLyricsAsync(song);

        result!.Lines.Should().ContainSingle().Which.Text.Should().Be("Salt on the window");
        await _fileSystem.Received(1).WriteAllTextAsync(Arg.Any<string>(), "[00:01.000]Salt on the window");
    }

    [Fact]
    public async Task GetLyricsAsync_ServerSongTheServerHasNoLyricsFor_LooksOnline()
    {
        var song = new Song { Title = "Undertow", FilePath = @"nagi-server:0123\s2", RemoteId = "s2" };
        _musicServers.GetLyricsAsync(song, Arg.Any<CancellationToken>()).Returns((string?)null);
        _settingsService.Set(CoreSettings.FetchOnlineLyrics, true);
        _pathConfig.LrcCachePath.Returns(@"C:\LyricsCache");
        _lrcLib.GetLyricsAsync(Arg.Any<Song>(), Arg.Any<CancellationToken>()).Returns(Found("[00:02.00]From LRCLIB"));

        var result = await _lrcService.GetLyricsAsync(song);

        result!.Lines.Should().ContainSingle().Which.Text.Should().Be("From LRCLIB");
    }

    [Fact]
    public async Task GetLyricsAsync_ServerSongWhileTheServerIsDown_IsAskedAgainLater()
    {
        var song = await SeedAsync(new Song { Title = "Breakwater", FilePath = @"nagi-server:0123\s1", RemoteId = "s1" });
        _settingsService.Set(CoreSettings.FetchOnlineLyrics, true);
        _musicServers.GetLyricsAsync(song, Arg.Any<CancellationToken>())
            .Returns(Task.FromException<string?>(new MusicServerException(MusicServerError.Unreachable, "offline")));

        (await _lrcService.GetLyricsAsync(song)).Should().BeNull();

        song.LyricsLastCheckedUtc.Should().BeNull();
        (await StoredAsync(song)).LyricsLastCheckedUtc.Should().BeNull();
    }

    [Fact]
    public async Task GetLyricsAsync_WhenOneProviderFails_DoesNotCacheNegativeResult()
    {
        var song = await SeedAsync(new Song { Id = Guid.NewGuid(), Title = "Retryable" });
        _settingsService.Set(CoreSettings.FetchOnlineLyrics, true);
        _lrcLib.GetLyricsAsync(Arg.Any<Song>(), Arg.Any<CancellationToken>())
            .Returns(ServiceResult<string>.FromTemporaryError(), Found("[00:01.00]Recovered"));

        (await _lrcService.GetLyricsAsync(song)).Should().BeNull();
        song.LyricsLastCheckedUtc.Should().BeNull();
        (await StoredAsync(song)).LyricsLastCheckedUtc.Should().BeNull();

        var retry = await _lrcService.GetLyricsAsync(song);
        retry.Should().NotBeNull();
        retry!.Lines.Should().ContainSingle(line => line.Text == "Recovered");
    }

    [Fact]
    public async Task GetLyricsAsync_WhenFirstProviderFails_UsesFallbackLyrics()
    {
        _settingsService.Set(CoreSettings.FetchOnlineLyrics, true);
        _lrcLib.GetLyricsAsync(Arg.Any<Song>(), Arg.Any<CancellationToken>())
            .Returns(ServiceResult<string>.FromTemporaryError());
        _netEase.GetLyricsAsync(Arg.Any<Song>(), Arg.Any<CancellationToken>())
            .Returns(Found("[00:01.00]Fallback"));

        var result = await _lrcService.GetLyricsAsync(new Song { Title = "Song" });

        result.Should().NotBeNull();
        result!.Lines.Should().ContainSingle(line => line.Text == "Fallback");
    }

    [Fact]
    public async Task GetLyricsAsync_WhenCancelledBeforeProviderCompletes_DoesNotCacheLyrics()
    {
        using var cts = new CancellationTokenSource();
        var song = await SeedAsync(new Song { Id = Guid.NewGuid(), Title = "Cancelled" });
        _settingsService.Set(CoreSettings.FetchOnlineLyrics, true);
        _lrcLib.GetLyricsAsync(Arg.Any<Song>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                cts.Cancel();
                return Task.FromResult(Found("[00:01.00]Too late"));
            });

        var result = await _lrcService.GetLyricsAsync(song, cts.Token);

        result.Should().BeNull();
        var stored = await StoredAsync(song);
        stored.LyricsLastCheckedUtc.Should().BeNull();
        stored.LrcFilePath.Should().BeNull();
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
        using var service = new LrcService(_fileSystem, [onlineService, netEaseService], _settingsService,
            _pathConfig, _db.ContextFactory, _musicServers, _logger);
        var song = await SeedAsync(new Song { Id = Guid.NewGuid(), Title = "Song", DurationTicks = TimeSpan.FromSeconds(180).Ticks });
        _settingsService.Set(CoreSettings.FetchOnlineLyrics, true);
        _settingsService.EnableOnly(CoreSettings.LyricsProviders, providerId);
        handler.SendAsyncFunc = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        (await service.GetLyricsAsync(song)).Should().BeNull();
        song.LyricsLastCheckedUtc.Should().BeNull();
        (await StoredAsync(song)).LyricsLastCheckedUtc.Should().BeNull();

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

        var result = await _lrcService.GetLyricsAsync(new Song { LrcFilePath = LrcPath });

        result!.IsSynced.Should().BeTrue();
        result.Lines.Should().HaveCount(2);
        result.Lines[0].Text.Should().Be("Line 1");
        result.Lines[0].StartTime.Should().Be(TimeSpan.FromSeconds(1));
        result.Lines[1].Text.Should().Be("Line 2");
        result.Lines[1].StartTime.Should().Be(TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData("file")]
    [InlineData("tags")]
    [InlineData("online")]
    public async Task GetLyricsAsync_PrefersTheLyricsFileThenTheTagsThenOnline(string source)
    {
        var song = new Song { Title = "Song", Lyrics = source == "online" ? null : "Embedded lyrics" };
        if (source == "file")
        {
            song.LrcFilePath = LrcPath;
            _fileSystem.FileExists(LrcPath).Returns(true);
            _fileSystem.ReadAllTextAsync(LrcPath).Returns("[00:01.00]From the file");
        }
        _settingsService.Set(CoreSettings.FetchOnlineLyrics, true);
        _lrcLib.GetLyricsAsync(Arg.Any<Song>(), Arg.Any<CancellationToken>()).Returns(Found("[00:01.00]From online"));

        var result = await _lrcService.GetLyricsAsync(song);

        result!.Lines[0].Text.Should().Be(source switch
        {
            "file" => "From the file",
            "tags" => "Embedded lyrics",
            _ => "From online"
        });
        await _lrcLib.Received(source == "online" ? 1 : 0).GetLyricsAsync(Arg.Any<Song>(), Arg.Any<CancellationToken>());
    }

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
    public async Task RemoveCachedLyricsAsync_DeletesOnlyManagedFileAndPreventsRedownload()
    {
        const string cacheRoot = @"C:\cache\lrc";
        var cachedPath = Path.Combine(cacheRoot, "song.lrc");
        var song = await SeedAsync(new Song { Id = Guid.NewGuid(), LrcFilePath = cachedPath });
        _pathConfig.LrcCachePath.Returns(cacheRoot);
        _fileSystem.FileExists(cachedPath).Returns(true);
        _settingsService.Set(CoreSettings.FetchOnlineLyrics, true);

        var removed = await _lrcService.RemoveCachedLyricsAsync(song);
        var result = await _lrcService.GetLyricsAsync(song);

        removed.Should().BeTrue();
        result.Should().BeNull();
        _fileSystem.Received(1).DeleteFile(cachedPath);
        var stored = await StoredAsync(song);
        stored.LrcFilePath.Should().BeNull();
        stored.LyricsLastCheckedUtc.Should().NotBeNull();
        await _lrcLib.DidNotReceive().GetLyricsAsync(Arg.Any<Song>(), Arg.Any<CancellationToken>());
        song.LrcFilePath.Should().BeNull();
        song.LyricsLastCheckedUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task SaveAndRemoveLyrics_WithExternalSidecar_RestoresSidecar()
    {
        const string cacheRoot = @"C:\cache\lrc";
        const string externalPath = @"C:\Music\Song.lrc";
        const string content = "[00:01.00]Correct lyrics";
        var song = await SeedAsync(new Song
        {
            Id = Guid.NewGuid(),
            FilePath = @"C:\Music\Song.flac",
            Title = "Song",
            PrimaryArtistName = "Artist",
            LrcFilePath = externalPath,
            Album = new Album { Title = "Album" }
        });
        var expectedPath = LrcCache.OverridePath(cacheRoot, song.FilePath);
        _pathConfig.LrcCachePath.Returns(cacheRoot);
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
        (await StoredAsync(song)).LrcFilePath.Should().Be(externalPath);
        song.LrcFilePath.Should().Be(externalPath);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SaveLyricsAsync_WhenWriteOrReplaceFails_PreservesExistingLyrics(bool failOnReplace)
    {
        const string cacheRoot = @"C:\cache\lrc";
        var song = await SeedAsync(new Song { FilePath = @"C:\Music\Song.flac" });
        var previousPath = LrcCache.OverridePath(cacheRoot, song.FilePath);
        song.LrcFilePath = previousPath;
        _pathConfig.LrcCachePath.Returns(cacheRoot);
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
        (await StoredAsync(song)).LrcFilePath.Should().BeNull();
        song.LrcFilePath.Should().Be(previousPath);
    }

    [Theory]
    [InlineData(@"C:\Music\Song.lrc")]
    [InlineData(@"C:\cache\lrc\..\Song.lrc")]
    [InlineData(@"C:\cache\lrc-other\Song.lrc")]
    public async Task RemoveCachedLyricsAsync_WithExternalSidecar_DoesNotDeleteFile(string path)
    {
        var song = await SeedAsync(new Song { Id = Guid.NewGuid(), LrcFilePath = path });
        _pathConfig.LrcCachePath.Returns(@"C:\cache\lrc");

        var removed = await _lrcService.RemoveCachedLyricsAsync(song);

        removed.Should().BeFalse();
        _fileSystem.DidNotReceive().DeleteFile(Arg.Any<string>());
        (await StoredAsync(song)).LrcFilePath.Should().Be(path);
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

    [Fact]
    public async Task GetLyricsAsync_WithNoLocalFile_FetchesBothInParallel_PrefersLrcLib()
    {
        var song = new Song { Title = "Test", LrcFilePath = null, Duration = TimeSpan.FromMinutes(3) };
        var preferredResult = new TaskCompletionSource<ServiceResult<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _settingsService.Set(CoreSettings.FetchOnlineLyrics, true);
        _lrcLib.GetLyricsAsync(Arg.Any<Song>(), Arg.Any<CancellationToken>()).Returns(preferredResult.Task);
        _netEase.GetLyricsAsync(Arg.Any<Song>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                preferredResult.SetResult(Found("[00:01.00]From LRCLIB"));
                return Task.FromResult(Found("[00:01.00]From NetEase"));
            });

        var result = await _lrcService.GetLyricsAsync(song).WaitAsync(TimeSpan.FromSeconds(5));
        result.Should().NotBeNull();
        result!.Lines.Should().HaveCount(1);
        result.Lines[0].Text.Should().Be("From LRCLIB");
        await _lrcLib.Received(1).GetLyricsAsync(Arg.Any<Song>(), Arg.Any<CancellationToken>());
        await _netEase.Received(1).GetLyricsAsync(Arg.Any<Song>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetLyricsAsync_WithLegacyCachePath_InvalidatesAndRefreshesCollisionProneFile()
    {
        const string cacheRoot = @"C:\cache\lrc";
        const string legacyPath = @"C:\cache\lrc\Artist - Album - Song.lrc";
        var song = await SeedAsync(new Song
        {
            Title = "Song",
            PrimaryArtistName = "Artist",
            FilePath = @"C:\Music\Song.flac",
            LrcFilePath = legacyPath,
            LyricsLastCheckedUtc = DateTime.UtcNow,
            Duration = TimeSpan.FromMinutes(3),
            Album = new Album { Title = "Album" }
        });
        _pathConfig.LrcCachePath.Returns(cacheRoot);
        _fileSystem.FileExists(legacyPath).Returns(true);
        _settingsService.Set(CoreSettings.FetchOnlineLyrics, true);
        _lrcLib.GetLyricsAsync(Arg.Any<Song>(), Arg.Any<CancellationToken>())
            .Returns(Found("[00:01.00]Fresh Lyrics"));
        _netEase.GetLyricsAsync(Arg.Any<Song>(), Arg.Any<CancellationToken>())
            .Returns(Found(null));

        var result = await _lrcService.GetLyricsAsync(song);

        result!.Lines.Single().Text.Should().Be("Fresh Lyrics");
        await _fileSystem.DidNotReceive().ReadAllTextAsync(legacyPath);
        (await StoredAsync(song)).LrcFilePath.Should().NotBeNull().And.NotBe(legacyPath).And.EndWith(".lrc");
    }

    [Fact]
    public async Task GetLyricsAsync_WithIdentityBasedCachePathAfterMetadataEdit_UsesLocalFile()
    {
        const string cacheRoot = @"C:\cache\lrc";
        const string audioFilePath = @"C:\Music\Song.flac";
        var cachePath = LrcCache.CachedPath(cacheRoot, audioFilePath, "Original Artist", "Original Album", "Original Title");
        var song = await SeedAsync(new Song
        {
            Title = "Corrected Title",
            PrimaryArtistName = "Corrected Artist",
            FilePath = audioFilePath,
            LrcFilePath = cachePath,
            LyricsLastCheckedUtc = DateTime.UtcNow,
            Album = new Album { Title = "Corrected Album" }
        });
        _pathConfig.LrcCachePath.Returns(cacheRoot);
        _fileSystem.FileExists(cachePath).Returns(true);
        _fileSystem.ReadAllTextAsync(cachePath).Returns("[00:01.00]Cached Lyrics");
        _settingsService.Set(CoreSettings.FetchOnlineLyrics, true);

        var result = await _lrcService.GetLyricsAsync(song);

        result!.Lines.Single().Text.Should().Be("Cached Lyrics");
        (await StoredAsync(song)).LrcFilePath.Should().Be(cachePath);
        await _lrcLib.DidNotReceive().GetLyricsAsync(Arg.Any<Song>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetLyricsAsync_WhenOnlineFetchDisabled_DoesNotCallRemoteServices()
    {
        var song = new Song { Title = "Test", LrcFilePath = null };
        _settingsService.Set(CoreSettings.FetchOnlineLyrics, false);

        var result = await _lrcService.GetLyricsAsync(song);

        result.Should().BeNull();
        await _lrcLib.DidNotReceive().GetLyricsAsync(Arg.Any<Song>(), Arg.Any<CancellationToken>());
        await _netEase.DidNotReceive().GetLyricsAsync(Arg.Any<Song>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetLyricsAsync_WhenNoProvidersEnabled_DoesNotFetchOrCacheNegativeResult()
    {
        var song = await SeedAsync(new Song { Title = "Test", LrcFilePath = null, Duration = TimeSpan.FromMinutes(3) });
        _settingsService.Set(CoreSettings.FetchOnlineLyrics, true);
        _settingsService.EnableOnly(CoreSettings.LyricsProviders);

        var result = await _lrcService.GetLyricsAsync(song);

        result.Should().BeNull();
        await _lrcLib.DidNotReceive().GetLyricsAsync(Arg.Any<Song>(), Arg.Any<CancellationToken>());
        await _netEase.DidNotReceive().GetLyricsAsync(Arg.Any<Song>(), Arg.Any<CancellationToken>());
        (await StoredAsync(song)).LyricsLastCheckedUtc.Should().BeNull();
        song.LyricsLastCheckedUtc.Should().BeNull();
    }

    [Theory]
    [InlineData(ServiceProviderIds.LrcLib, "From LRCLIB")]
    [InlineData(ServiceProviderIds.NetEase, "From NetEase")]
    public async Task GetLyricsAsync_WithOneEnabledProvider_OnlyCallsThatProvider(string providerId, string expectedText)
    {
        var song = new Song { Title = "Test", Duration = TimeSpan.FromMinutes(3) };
        _settingsService.Set(CoreSettings.FetchOnlineLyrics, true);
        _settingsService.EnableOnly(CoreSettings.LyricsProviders, providerId);
        _lrcLib.GetLyricsAsync(Arg.Any<Song>(), Arg.Any<CancellationToken>()).Returns(Found("[00:01.00]From LRCLIB"));
        _netEase.GetLyricsAsync(Arg.Any<Song>(), Arg.Any<CancellationToken>())
            .Returns(Found("[00:01.00]From NetEase"));

        var result = await _lrcService.GetLyricsAsync(song);

        result!.Lines[0].Text.Should().Be(expectedText);
        await _lrcLib.Received(providerId == ServiceProviderIds.LrcLib ? 1 : 0)
            .GetLyricsAsync(Arg.Any<Song>(), Arg.Any<CancellationToken>());
        await _netEase.Received(providerId == ServiceProviderIds.NetEase ? 1 : 0)
            .GetLyricsAsync(Arg.Any<Song>(), Arg.Any<CancellationToken>());
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
            TimeSpan.FromMilliseconds(1250),
            TimeSpan.FromMilliseconds(2250));
    }

    [Fact]
    public async Task GetLyricsAsync_WhenLyricsAlreadyChecked_ReturnsNullWithoutFetching()
    {
        var song = new Song { Title = "Test", LrcFilePath = null, LyricsLastCheckedUtc = DateTime.UtcNow };
        _settingsService.Set(CoreSettings.FetchOnlineLyrics, true);

        var result = await _lrcService.GetLyricsAsync(song);

        result.Should().BeNull();
        await _lrcLib.DidNotReceive().GetLyricsAsync(Arg.Any<Song>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetLyricsAsync_WhenAlreadyCancelled_DoesNotCallOnlineServices()
    {
        var song = new Song { Title = "Test", LrcFilePath = null, Duration = TimeSpan.FromMinutes(3) };
        _settingsService.Set(CoreSettings.FetchOnlineLyrics, true);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await _lrcService.GetLyricsAsync(song, cts.Token);
        result.Should().BeNull();
        await _lrcLib.DidNotReceive().GetLyricsAsync(Arg.Any<Song>(), Arg.Any<CancellationToken>());
        await _netEase.DidNotReceive().GetLyricsAsync(Arg.Any<Song>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetLyricsAsync_WithLocalFile_DoesNotFetchOnline()
    {
        var song = new Song { Title = "Test", LrcFilePath = LrcPath };
        _fileSystem.FileExists(LrcPath).Returns(true);
        _fileSystem.ReadAllTextAsync(LrcPath).Returns("[00:01.00]Local Lyrics");
        _settingsService.Set(CoreSettings.FetchOnlineLyrics, true);

        var result = await _lrcService.GetLyricsAsync(song);

        result.Should().NotBeNull();
        result!.Lines[0].Text.Should().Be("Local Lyrics");
        await _lrcLib.DidNotReceive().GetLyricsAsync(Arg.Any<Song>(), Arg.Any<CancellationToken>());
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

        _settingsService.Set(CoreSettings.FetchOnlineLyrics, true);
        _lrcLib.GetLyricsAsync(Arg.Any<Song>(), Arg.Any<CancellationToken>()).Returns(Found("[00:01.00]Found"));

        await _lrcService.GetLyricsAsync(song);
        await _lrcLib.Received(1).GetLyricsAsync(
            Arg.Is<Song>(s => s.Title == "Multi Artist Track" && s.PrimaryArtistName == "Primary Artist"),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetLyricsAsync_WhenFetchDisabled_StopsWaitingForProvider(bool plainFound)
    {
        var song = await SeedAsync(new Song { Title = "Song" });
        _settingsService.Set(CoreSettings.FetchOnlineLyrics, true);
        var pending = new TaskCompletionSource<ServiceResult<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken providerToken = default;
        Task<ServiceResult<string>> Hold(CancellationToken token)
        {
            providerToken = token;
            return pending.Task;
        }
        if (plainFound)
        {
            _lrcLib.GetLyricsAsync(Arg.Any<Song>(), Arg.Any<CancellationToken>()).Returns(Found("Plain lyrics"));
            _netEase.GetLyricsAsync(Arg.Any<Song>(), Arg.Any<CancellationToken>())
                .Returns(call => Hold(call.Arg<CancellationToken>()));
        }
        else
        {
            _lrcLib.GetLyricsAsync(Arg.Any<Song>(), Arg.Any<CancellationToken>())
                .Returns(call => Hold(call.Arg<CancellationToken>()));
        }

        var fetch = _lrcService.GetLyricsAsync(song);
        _settingsService.Set(CoreSettings.FetchOnlineLyrics, false);
        try
        {
            var result = await fetch.WaitAsync(TimeSpan.FromSeconds(5));

            providerToken.IsCancellationRequested.Should().BeTrue();
            result.Should().BeNull();
            (await StoredAsync(song)).LyricsLastCheckedUtc.Should().BeNull();
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
        _settingsService.Set(CoreSettings.FetchOnlineLyrics, true);
        _lrcLib.GetLyricsAsync(Arg.Any<Song>(), Arg.Any<CancellationToken>()).Returns(Found("[00:01.00]Lyric"));

        _settingsService.Set(CoreSettings.FetchOnlineLyrics, false);
        _settingsService.Set(CoreSettings.FetchOnlineLyrics, true);
        var result = await _lrcService.GetLyricsAsync(song);

        result.Should().NotBeNull();
        await _lrcLib.Received(1).GetLyricsAsync(Arg.Any<Song>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetLyricsAsync_WhenProvidersReturnEmpty_MarksLyricsAsChecked()
    {
        var song = await SeedAsync(new Song { Title = "Unknown Song" });
        _settingsService.Set(CoreSettings.FetchOnlineLyrics, true);
        _lrcLib.GetLyricsAsync(Arg.Any<Song>(), Arg.Any<CancellationToken>()).Returns(Found(null));
        _netEase.GetLyricsAsync(Arg.Any<Song>(), Arg.Any<CancellationToken>())
            .Returns(Found(null));

        var result = await _lrcService.GetLyricsAsync(song);
        result.Should().BeNull();
        (await StoredAsync(song)).LyricsLastCheckedUtc.Should().NotBeNull();
    }

    private static ILyricsProvider LyricsProvider(string id)
    {
        var provider = Substitute.For<ILyricsProvider>();
        provider.Id.Returns(id);
        provider.GetLyricsAsync(Arg.Any<Song>(), Arg.Any<CancellationToken>()).Returns(Found(null));
        return provider;
    }

    private static ServiceResult<string> Found(string? lyrics) => ServiceResult<string>.FromSuccessOrNotFound(lyrics);
}
