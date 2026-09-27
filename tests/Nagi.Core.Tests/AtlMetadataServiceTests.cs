using ATL;
using FluentAssertions;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Nagi.Core.Models;
using Nagi.Core.Tests.Utils;
using Nagi.Core.Helpers;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Implementations;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Nagi.Core.Tests;

public class AtlMetadataServiceTests : IDisposable
{
    private const string LrcCachePath = "C:\\cache\\lrc";
    private readonly IFileSystemService _fileSystem;
    private readonly IImageProcessor _imageProcessor;
    private readonly ILogger<AtlMetadataService> _logger;
    private readonly ISettingsService _settingsService;
    private readonly AtlMetadataService _metadataService;
    private readonly IPathConfiguration _pathConfig;
    private readonly string _tempDirectory;

    // A valid MP3 file with proper headers - this is the minimum valid MP3 structure
    // that ATL.NET can read and write tags to
    private static readonly byte[] ValidMp3Bytes = CreateValidMp3Bytes();

    public AtlMetadataServiceTests()
    {
        _imageProcessor = Substitute.For<IImageProcessor>();
        _fileSystem = Substitute.For<IFileSystemService>();
        _pathConfig = Substitute.For<IPathConfiguration>();
        _logger = Substitute.For<ILogger<AtlMetadataService>>();
        _settingsService = Substitute.For<ISettingsService>();

        _tempDirectory = Directory.CreateTempSubdirectory("Nagi-AtlTests-").FullName;

        _pathConfig.LrcCachePath.Returns(LrcCachePath);

        _fileSystem.GetFileNameWithoutExtension(Arg.Any<string>())
            .Returns(callInfo => Path.GetFileNameWithoutExtension(callInfo.ArgAt<string>(0)));
        _fileSystem.GetDirectoryName(Arg.Any<string>())
            .Returns(callInfo => Path.GetDirectoryName(callInfo.ArgAt<string>(0)));
        _fileSystem.Combine(Arg.Any<string[]>())
            .Returns(callInfo => Path.Combine(callInfo.ArgAt<string[]>(0)));
        _fileSystem.GetExtension(Arg.Any<string>())
            .Returns(callInfo => Path.GetExtension(callInfo.ArgAt<string>(0)));

        _settingsService.GetArtistSplitCharactersAsync().Returns(Task.FromResult(string.Empty));
        _settingsService.GetGenreSplitCharactersAsync().Returns(Task.FromResult(string.Empty));

        _metadataService = new AtlMetadataService(_imageProcessor, _fileSystem, _pathConfig, _logger, _settingsService);
    }
    private static byte[] CreateValidMp3Bytes()
    {
        // Create a minimal but valid MP3 file structure
        // This includes an ID3v2 header followed by a minimal audio frame
        using var ms = new MemoryStream();

        // ID3v2.3 header (10 bytes)
        ms.Write(new byte[] { 0x49, 0x44, 0x33 }); // "ID3"
        ms.Write(new byte[] { 0x03, 0x00 }); // Version 2.3
        ms.Write(new byte[] { 0x00 }); // Flags
        ms.Write(new byte[] { 0x00, 0x00, 0x00, 0x00 }); // Size (syncsafe, 0 for now)

        // Add some minimal MP3 audio frames (silent frames)
        // MP3 frame header: sync word + audio info
        for (var i = 0; i < 10; i++)
        {
            // MP3 frame sync (0xFFE for MPEG Audio Layer 3)
            ms.Write(new byte[] { 0xFF, 0xFB, 0x90, 0x00 }); // MPEG1 Layer3, 128kbps, 44100Hz
            // Fill with zeros for the rest of the frame (417 bytes for this config)
            ms.Write(new byte[413]);
        }

        return ms.ToArray();
    }

    public void Dispose() => Directory.Delete(_tempDirectory, true);
    private string CreateTestAudioFile(string fileName, Action<Track> trackSetup)
    {
        var filePath = Path.Combine(_tempDirectory, fileName);
        File.WriteAllBytes(filePath, ValidMp3Bytes);

        var track = new Track(filePath);
        trackSetup(track);
        track.Save();

        _fileSystem.GetFileInfo(filePath).Returns(new FileInfo(filePath));
        _fileSystem.FileExists(filePath).Returns(true);

        return filePath;
    }

    [Fact]
    public async Task ExtractMetadataAsync_WithFullMetadata_ExtractsAllPropertiesCorrectly()
    {
        var pictureData = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }; // PNG header
        var filePath = CreateTestAudioFile("full.mp3", track =>
        {
            track.Title = "Test Title";
            track.Artist = "Test Artist";
            track.AlbumArtist = "Test Album Artist";
            track.Album = "Test Album";
            track.Year = 2023;
            track.TrackNumber = 5;
            track.TrackTotal = 10;
            track.DiscNumber = 1;
            track.DiscTotal = 2;
            track.BPM = 120;
            track.Lyrics.Add(new LyricsInfo { UnsynchronizedLyrics = "Some simple lyrics" });
            track.Genre = "Rock;Pop";
            track.AdditionalFields.Add("GROUPING", "Test Grouping");
            track.EmbeddedPictures.Add(PictureInfo.fromBinaryData(pictureData));
        });

        _imageProcessor.SaveCoverArtAndExtractColorsAsync(Arg.Any<byte[]>())
            .Returns(Task.FromResult<(string?, string?, string?)>(("C:/art/1.jpg", "light1", "dark1")));
        var result = await _metadataService.ExtractMetadataAsync(filePath);
        result.Should().NotBeNull();
        result.ExtractionFailed.Should().BeFalse();
        result.Title.Should().Be("Test Title");
        result.Artists.Should().ContainSingle().Which.Should().Be("Test Artist");
        result.AlbumArtists.Should().ContainSingle().Which.Should().Be("Test Album Artist");
        result.Album.Should().Be("Test Album");
        result.Year.Should().Be(2023);
        result.TrackNumber.Should().Be(5);
        result.TrackCount.Should().Be(10);
        result.DiscNumber.Should().Be(1);
        result.DiscCount.Should().Be(2);
        result.Bpm.Should().Be(120);
        result.Lyrics.Should().Be("Some simple lyrics");
        result.Genres.Should().ContainSingle().Which.Should().Be("Rock;Pop");
        result.CoverArtUri.Should().Be("C:/art/1.jpg");
        result.Grouping.Should().Be("Test Grouping");
        result.LightSwatchId.Should().Be("light1");
        result.DarkSwatchId.Should().Be("dark1");
    }

    [Fact]
    public async Task ExtractMetadataAsync_WithCustomGenreSplitCharacters_SplitsGenresCorrectly()
    {
        var filePath = CreateTestAudioFile("genresplit.mp3", track =>
        {
            track.Genre = "Rock;Pop/Indie\\Jazz";
        });

        _settingsService.GetGenreSplitCharactersAsync().Returns(Task.FromResult(";/\\"));
        var result = await _metadataService.ExtractMetadataAsync(filePath);
        result.Genres.Should().ContainInOrder("Rock", "Pop", "Indie", "Jazz");
    }

    [Fact]
    public async Task ExtractMetadataAsync_WithNativeMultiValueGenres_PreservesGenreBoundaries()
    {
        var filePath = CreateTestAudioFile("multigenre.mp3", track =>
        {
            track.Genre = "2-Step\u02F5Funky Breaks";
        });

        _settingsService.GetGenreSplitCharactersAsync().Returns(Task.FromResult("\\"));

        var result = await _metadataService.ExtractMetadataAsync(filePath);

        result.Genres.Should().ContainInOrder("2-Step", "Funky Breaks");
    }

    [Fact]
    public async Task ExtractMetadataAsync_WithMinimalMetadata_ProvidesSaneDefaults()
    {
        var filePath = CreateTestAudioFile("minimal.mp3", track =>
        {
            track.Title = "";
            track.Artist = "";
            track.Album = "";
        });
        var result = await _metadataService.ExtractMetadataAsync(filePath);
        result.Should().NotBeNull();
        result.Title.Should().Be("minimal");
        result.Artists.Should().ContainSingle().Which.Should().Be("Unknown Artist");
        result.Album.Should().Be("Unknown Album");
        result.AlbumArtists.Should().ContainSingle().Which.Should().Be("Unknown Artist");
        result.Year.Should().BeNull();
        result.CoverArtUri.Should().BeNull();
    }

    [Fact]
    public async Task ProcessAlbumArtAsync_WhenProcessingFails_AllowsRetryOnNextCall()
    {
        var pictureData = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }; // PNG header
        var filePath = CreateTestAudioFile("failsong.mp3", track =>
        {
            track.Album = "Failing Album";
            track.AlbumArtist = "Failing Artist";
            track.EmbeddedPictures.Add(PictureInfo.fromBinaryData(pictureData));
        });

        _imageProcessor.SaveCoverArtAndExtractColorsAsync(Arg.Any<byte[]>())
            .ThrowsAsync(new InvalidOperationException("Processing failed"));
        var result1 = await _metadataService.ExtractMetadataAsync(filePath);
        result1.CoverArtUri.Should().BeNull();
        await _imageProcessor.Received(1)
            .SaveCoverArtAndExtractColorsAsync(Arg.Any<byte[]>());
        _imageProcessor.ClearReceivedCalls();
        _imageProcessor.SaveCoverArtAndExtractColorsAsync(Arg.Any<byte[]>())
            .Returns(Task.FromResult<(string?, string?, string?)>(("C:/art/success.jpg", "lightOK", "darkOK")));
        var result2 = await _metadataService.ExtractMetadataAsync(filePath);
        await _imageProcessor.Received(1)
            .SaveCoverArtAndExtractColorsAsync(Arg.Any<byte[]>());
        result2.CoverArtUri.Should().Be("C:/art/success.jpg");
    }

    [Fact]
    public async Task GetLrcPathAsync_WithValidCache_ReturnsCachedPathWithoutExtraction()
    {
        var audioFilePath = CreateTestAudioFile("cached.mp3", track =>
        {
            track.Title = "Cached Song";
            track.Artist = "Cache Artist";
        });
        var audioFileTime = new DateTime(2023, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var cacheFileTime = audioFileTime.AddHours(1);

        File.SetLastWriteTimeUtc(audioFilePath, audioFileTime);
        _fileSystem.GetFileInfo(audioFilePath).Returns(new FileInfo(audioFilePath));

        var expectedCachePath = Path.Combine(LrcCachePath,
            FileNameHelper.GenerateLrcCacheFileName(audioFilePath, "Cache Artist", null, "Cached Song"));

        _fileSystem.FileExists(expectedCachePath).Returns(true);
        _fileSystem.GetLastWriteTimeUtc(expectedCachePath).Returns(cacheFileTime);
        var result = await _metadataService.ExtractMetadataAsync(audioFilePath);
        result.LrcFilePath.Should().Be(expectedCachePath);
        await _fileSystem.DidNotReceive().WriteAllTextAsync(Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task GetLrcPathAsync_WithOlderOverride_ReturnsOverride()
    {
        var audioFilePath = CreateTestAudioFile("override.mp3", track =>
        {
            track.Title = "Override Song";
            track.Artist = "Override Artist";
        });
        _fileSystem.GetFileInfo(audioFilePath).Returns(new FileInfo(audioFilePath));
        var expectedPath = Path.Combine(LrcCachePath,
            FileNameHelper.GenerateLrcOverrideFileName(audioFilePath));
        _fileSystem.FileExists(expectedPath).Returns(true);

        var result = await _metadataService.ExtractMetadataAsync(audioFilePath);

        result.LrcFilePath.Should().Be(expectedPath);
        _fileSystem.DidNotReceive().GetLastWriteTimeUtc(expectedPath);
    }

    [Fact]
    public async Task FindLrcFilePath_WithExternalLrcFile_ReturnsExternalPath()
    {
        var audioFilePath = CreateTestAudioFile("external.mp3", track =>
        {
            /* no lyrics */
        });
        var expectedLrcPath = Path.Combine(_tempDirectory, "external.lrc");

        _fileSystem.FileExists(Arg.Is<string>(s => s != null && s.Contains(LrcCachePath))).Returns(false);
        _fileSystem.GetFiles(_tempDirectory, "*.lrc").Returns(new[] { expectedLrcPath });
        _fileSystem.GetFileNameWithoutExtension(expectedLrcPath).Returns("external");
        var result = await _metadataService.ExtractMetadataAsync(audioFilePath);
        result.LrcFilePath.Should().Be(expectedLrcPath);
    }

    [Fact]
    public async Task FindLrcFilePath_WithExternalTxtFile_ReturnsTxtPath()
    {
        var audioFilePath = CreateTestAudioFile("externaltxt.mp3", track =>
        {
            /* no lyrics */
        });
        var expectedTxtPath = Path.Combine(_tempDirectory, "externaltxt.txt");

        _fileSystem.FileExists(Arg.Is<string>(s => s != null && s.Contains(LrcCachePath))).Returns(false);
        _fileSystem.GetFiles(_tempDirectory, "*.lrc").Returns(Array.Empty<string>());
        _fileSystem.GetFiles(_tempDirectory, "*.txt").Returns(new[] { expectedTxtPath });
        _fileSystem.GetFileNameWithoutExtension(expectedTxtPath).Returns("externaltxt");
        var result = await _metadataService.ExtractMetadataAsync(audioFilePath);
        result.LrcFilePath.Should().Be(expectedTxtPath);
    }

    [Fact]
    public async Task ExtractMetadataAsync_WithCorruptFile_HandlesGracefully()
    {
        var filePath = Path.Combine(_tempDirectory, "corrupt.mp3");
        File.WriteAllBytes(filePath, new byte[] { 0xFF, 0xFB, 0x00 });
        _fileSystem.GetFileInfo(filePath).Returns(new FileInfo(filePath));
        _fileSystem.FileExists(filePath).Returns(true);
        var result = await _metadataService.ExtractMetadataAsync(filePath);
        result.Should().NotBeNull();
        result.ExtractionFailed.Should().BeTrue();
        result.ErrorMessage.Should().BeOneOf("CorruptFile", "UnsupportedFormat");
    }

    [Fact]
    public async Task ExtractMetadataAsync_WithUnsupportedFormat_HandlesGracefully()
    {
        var filePath = Path.Combine(_tempDirectory, "document.txt");
        File.WriteAllText(filePath, "this is not a music file");
        _fileSystem.GetFileInfo(filePath).Returns(new FileInfo(filePath));
        _fileSystem.FileExists(filePath).Returns(true);
        var result = await _metadataService.ExtractMetadataAsync(filePath);
        result.Should().NotBeNull();
        result.ExtractionFailed.Should().BeTrue();
        result.ErrorMessage.Should().BeOneOf("CorruptFile", "UnsupportedFormat");
    }

    [Fact]
    public async Task ProcessAlbumArtAsync_WithDirectoryCoverArt_UsesFallback()
    {
        var audioFilePath = CreateTestAudioFile("nocoverart.mp3", track =>
        {
            track.Title = "No Cover Song";
            track.Artist = "No Cover Artist";
        });

        var coverArtPath = Path.Combine(_tempDirectory, "cover.jpg");
        var coverArtBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }; // PNG header

        _fileSystem.GetFiles(_tempDirectory, "*.*").Returns(new[] { coverArtPath });
        _fileSystem.GetExtension(coverArtPath).Returns(".jpg");
        _fileSystem.GetFileNameWithoutExtension(coverArtPath).Returns("cover");
        _fileSystem.ReadAllBytesAsync(coverArtPath).Returns(Task.FromResult(coverArtBytes));

        _imageProcessor.SaveCoverArtAndExtractColorsAsync(Arg.Any<byte[]>())
            .Returns(Task.FromResult<(string?, string?, string?)>(("C:/art/cover.jpg", "lightCover", "darkCover")));
        var result = await _metadataService.ExtractMetadataAsync(audioFilePath);
        result.CoverArtUri.Should().Be("C:/art/cover.jpg");
        result.LightSwatchId.Should().Be("lightCover");
        result.DarkSwatchId.Should().Be("darkCover");
        await _fileSystem.Received(1).ReadAllBytesAsync(coverArtPath);
    }

    [Fact]
    public async Task ProcessAlbumArtAsync_DirectoryArtTakesPriorityOverEmbeddedArt()
    {
        var embeddedPictureData = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }; // PNG header
        var audioFilePath = CreateTestAudioFile("botharts.mp3", track =>
        {
            track.Title = "Both Arts Song";
            track.EmbeddedPictures.Add(PictureInfo.fromBinaryData(embeddedPictureData));
        });

        var coverJpgPath = Path.Combine(_tempDirectory, "cover.jpg");
        var directoryArtBytes = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }; // JPEG SOI header (distinct from embedded)
        _fileSystem.GetFiles(_tempDirectory, "*.*").Returns(new[] { coverJpgPath });
        _fileSystem.GetExtension(coverJpgPath).Returns(".jpg");
        _fileSystem.GetFileNameWithoutExtension(coverJpgPath).Returns("cover");
        _fileSystem.ReadAllBytesAsync(coverJpgPath).Returns(Task.FromResult(directoryArtBytes));

        _imageProcessor.SaveCoverArtAndExtractColorsAsync(directoryArtBytes)
            .Returns(Task.FromResult<(string?, string?, string?)>(("C:/art/directory.jpg", "dir-light", "dir-dark")));
        _imageProcessor.SaveCoverArtAndExtractColorsAsync(embeddedPictureData)
            .Returns(Task.FromResult<(string?, string?, string?)>(("C:/art/embedded.jpg", "emb-light", "emb-dark")));
        var result = await _metadataService.ExtractMetadataAsync(audioFilePath);
        result.CoverArtUri.Should().Be("C:/art/directory.jpg", because: "directory art has higher priority than embedded");
        result.LightSwatchId.Should().Be("dir-light");
        await _imageProcessor.DidNotReceive().SaveCoverArtAndExtractColorsAsync(embeddedPictureData);
    }

    [Theory]
    [InlineData("folder", "cover")]
    [InlineData("album", "front")]
    public async Task ProcessAlbumArtAsync_SelectsCoverByPriorityRatherThanEnumerationOrder(string otherName, string preferredName)
    {
        var audioPath = CreateTestAudioFile("cover-priority.mp3", _ => { });
        var otherPath = Path.Combine(_tempDirectory, otherName + ".jpg");
        var preferredPath = Path.Combine(_tempDirectory, preferredName + ".jpg");
        byte[] preferredBytes = [0x11, 0x22, 0x33];
        _fileSystem.GetFiles(_tempDirectory, "*.*").Returns([otherPath, preferredPath]);
        _fileSystem.ReadAllBytesAsync(preferredPath).Returns(preferredBytes);
        _imageProcessor.SaveCoverArtAndExtractColorsAsync(preferredBytes)
            .Returns(("C:/art/preferred.jpg", "light", "dark"));

        var result = await _metadataService.ExtractMetadataAsync(audioPath);

        result.CoverArtUri.Should().Be("C:/art/preferred.jpg");
        await _fileSystem.Received(1).ReadAllBytesAsync(preferredPath);
        await _fileSystem.DidNotReceive().ReadAllBytesAsync(otherPath);
    }

    [Fact]
    public async Task ProcessAlbumArtAsync_DirectoryScanException_FallsBackToEmbeddedArt()
    {
        byte[] embeddedBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        var audioPath = CreateTestAudioFile("scan-failure.mp3", track =>
            track.EmbeddedPictures.Add(PictureInfo.fromBinaryData(embeddedBytes)));
        _fileSystem.GetFiles(_tempDirectory, "*.*").Throws(new IOException("Access denied"));
        _imageProcessor.SaveCoverArtAndExtractColorsAsync(Arg.Any<byte[]>())
            .Returns(("C:/art/embedded.jpg", "light", "dark"));

        var result = await _metadataService.ExtractMetadataAsync(audioPath);

        result.CoverArtUri.Should().Be("C:/art/embedded.jpg");
        await _imageProcessor.Received(1).SaveCoverArtAndExtractColorsAsync(Arg.Any<byte[]>());
    }

    [Fact]
    public async Task ExtractAndCacheEmbeddedLrcAsync_PreservesLongTimestampsAndReplacesCacheAfterWriting()
    {
        var audioPath = CreateTestAudioFile("synced.mp3", track =>
        {
            track.Title = "Synced Song";
            track.Artist = "Synced Artist";
            track.Lyrics.Add(new LyricsInfo
            {
                SynchronizedLyrics =
                [
                    new LyricsInfo.LyricsPhrase(3600120, "Second line"),
                    new LyricsInfo.LyricsPhrase(1120, "First line")
                ]
            });
        });
        var expectedPath = Path.Combine(LrcCachePath,
            FileNameHelper.GenerateLrcCacheFileName(audioPath, "Synced Artist", null, "Synced Song"));
        string? writtenContent = null;
        _fileSystem.WriteAllTextAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(call =>
        {
            writtenContent = call.ArgAt<string>(1);
            return Task.CompletedTask;
        });

        var result = await _metadataService.ExtractMetadataAsync(audioPath);

        _fileSystem.Received(1).CreateDirectory(LrcCachePath);
        await _fileSystem.Received(1).WriteAllTextAsync(
            Arg.Is<string>(path => path.StartsWith(expectedPath + ".") && path.EndsWith(".tmp")), Arg.Any<string>());
        _fileSystem.Received(1).MoveFile(Arg.Any<string>(), expectedPath, true);
        var lines = ModernLrc.LrcParser.Parse(writtenContent!).Document.Lines;
        lines.Select(line => line.Timestamp.ToTimeSpan()).Should().Equal(
            TimeSpan.FromMilliseconds(1120), TimeSpan.FromMilliseconds(3600120));
        result.LrcFilePath.Should().Be(expectedPath);
    }

    [Fact]
    public async Task ReplayGainWriting_WithCommaDecimalCulture_RoundTripsThroughMetadata()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var filePath = CreateTestAudioFile("replaygain-culture.mp3", track => track.Title = "ReplayGain");
            using var database = new DbContextFactoryTestHelper();
            var song = new Song
            {
                FilePath = filePath,
                Folder = new Folder { Path = _tempDirectory }
            };
            await using (var context = database.ContextFactory.CreateDbContext())
            {
                context.Songs.Add(song);
                await context.SaveChangesAsync();
            }

            var analyzer = Substitute.For<ILoudnessAnalyzer>();
            analyzer.AnalyzeAsync(filePath, Arg.Any<CancellationToken>()).Returns((-9.03, 0.5));
            using var service = new ReplayGainService(database.ContextFactory, analyzer,
                Substitute.For<ILogger<ReplayGainService>>());

            await service.ScanLibraryAsync();

            await using var savedContext = database.ContextFactory.CreateDbContext();
            var savedSong = await savedContext.Songs.FindAsync(song.Id);
            savedSong!.ReplayGainTrackGain.Should().NotBeNull();
            var metadata = await _metadataService.ExtractMetadataAsync(filePath);
            metadata.ReplayGainTrackGain.Should().BeApproximately(savedSong.ReplayGainTrackGain!.Value, 0.005);
            metadata.ReplayGainTrackPeak.Should().BeApproximately(0.5, 0.000001);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Theory]
    [InlineData("-6.54 dB", "0.985432", -6.54, 0.985432)]
    [InlineData("+2.5dB (more info)", "1.1 peak value", 2.5, 1.1)]
    [InlineData("+2.5e-1 dB", "9.85432e-3", 0.25, 0.00985432)]
    public async Task ExtractMetadataAsync_WithReplayGainTags_ExtractsCorrectly(string gain, string peak, double expectedGain, double expectedPeak)
    {
        var filePath = CreateTestAudioFile("replaygain.mp3", track =>
        {
            track.Title = "ReplayGain Song";
            track.AdditionalFields.Add("REPLAYGAIN_TRACK_GAIN", gain);
            track.AdditionalFields.Add("REPLAYGAIN_TRACK_PEAK", peak);
        });
        var result = await _metadataService.ExtractMetadataAsync(filePath);
        result.ReplayGainTrackGain.Should().Be(expectedGain);
        result.ReplayGainTrackPeak.Should().Be(expectedPeak);
    }

    [Fact]
    public async Task ExtractMetadataAsync_WithMultiArtistDelimiters_SplitsCorrectlyAndAlignsLrc()
    {
        var filePath = CreateTestAudioFile("multiartist.mp3", track =>
        {
            track.Title = "Multi Artist Song";
            track.Artist = "Artist A / Artist B; Artist C; Artist D";
            track.Album = "Multi Album";
        });

        _settingsService.GetArtistSplitCharactersAsync().Returns(";/\\");

        var expectedCacheFileName = FileNameHelper.GenerateLrcCacheFileName(
            filePath, "Artist A", "Multi Album", "Multi Artist Song");
        var expectedCachePath = Path.Combine(LrcCachePath, expectedCacheFileName);

        _fileSystem.FileExists(expectedCachePath).Returns(true);
        _fileSystem.GetLastWriteTimeUtc(expectedCachePath).Returns(DateTime.UtcNow.AddHours(1));
        var result = await _metadataService.ExtractMetadataAsync(filePath);
        result.Artists.Should().HaveCount(4);
        result.Artists.Should().ContainInOrder("Artist A", "Artist B", "Artist C", "Artist D");
        result.LrcFilePath.Should().Be(expectedCachePath);
    }

    [Fact]
    public async Task ExtractMetadataAsync_WithEmptySplitCharacters_DoesNotSplitArtists()
    {
        var filePath = CreateTestAudioFile("nosplit.mp3", track =>
        {
            track.Artist = "Artist A / Artist B";
        });

        _settingsService.GetArtistSplitCharactersAsync().Returns("");
        var result = await _metadataService.ExtractMetadataAsync(filePath);
        result.Artists.Should().ContainSingle().Which.Should().Be("Artist A / Artist B");
    }

    [Fact]
    public async Task ExtractMetadataAsync_WhenSplitSettingsChangeDuringRead_NextScanUsesNewSettings()
    {
        var filePath = CreateTestAudioFile("settings-change.mp3", track =>
        {
            track.Artist = "Artist A / Artist B";
            track.Genre = "Rock/Pop";
        });
        var readStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldSetting = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _settingsService.GetArtistSplitCharactersAsync().Returns(_ =>
        {
            readStarted.TrySetResult();
            return oldSetting.Task;
        });

        var firstScan = _metadataService.ExtractMetadataAsync(filePath, includeMediaAssets: false);
        await readStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        _settingsService.GetArtistSplitCharactersAsync().Returns("/");
        _settingsService.GetGenreSplitCharactersAsync().Returns("/");
        oldSetting.SetResult("");
        await firstScan;

        var result = await _metadataService.ExtractMetadataAsync(filePath, includeMediaAssets: false);

        result.Artists.Should().Equal("Artist A", "Artist B");
        result.Genres.Should().Equal("Rock", "Pop");
    }
}
