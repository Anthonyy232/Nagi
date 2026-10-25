using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nagi.Core.Helpers;
using Nagi.Core.Models.Lyrics;
using Nagi.Core.Models.Romanization;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Implementations.Romanization;
using Nagi.Core.Tests.Utils;
using NSubstitute;
using Xunit;

namespace Nagi.Core.Tests;

public sealed class RomanizationPackTests : IDisposable
{
    private readonly string _tempRoot = Directory.CreateTempSubdirectory("NagiRomanizationTests-").FullName;
    private readonly List<HttpClient> _clients = new();

    public void Dispose()
    {
        foreach (var client in _clients) client.Dispose();
        Directory.Delete(_tempRoot, true);
    }

    [Fact]
    public async Task InstallPackAsync_WithValidPack_InstallsReplacesAndRemovesPack()
    {
        var packBytes = CreateDevanagariPack();
        var manager = CreateManager(CreateCatalogJson(CreateEntry("hindi", "devanagari-v1", packBytes)), packBytes);

        var installResult = await manager.InstallPackAsync("hindi");

        installResult.Succeeded.Should().BeTrue();
        var installed = await manager.GetInstalledPacksAsync();
        installed.Should().ContainSingle(p => p.Manifest.Id == "hindi");

        var installedPath = installed[0].DirectoryPath;
        var obsoletePath = Path.Combine(installedPath, "obsolete.txt");
        await File.WriteAllTextAsync(obsoletePath, "old pack file");
        (await manager.InstallPackAsync("hindi")).Succeeded.Should().BeTrue();
        File.Exists(obsoletePath).Should().BeFalse();
        Directory.EnumerateFileSystemEntries(Path.GetDirectoryName(installedPath)!).Should().Equal(installedPath);

        var removeResult = await manager.RemovePackAsync("hindi");

        removeResult.Succeeded.Should().BeTrue();
        (await manager.GetInstalledPacksAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task InstallPackAsync_StreamsPacksBeyondTheHttpClientBufferLimit()
    {
        var packBytes = CreateDevanagariPack(("data/extra.txt", Convert.ToHexString(RandomNumberGenerator.GetBytes(8192))));
        packBytes.Length.Should().BeGreaterThan(4096);
        var manager = CreateManager(CreateCatalogJson(CreateEntry("hindi", "devanagari-v1", packBytes)),
            packBytes, maxResponseContentBufferSize: 4096);

        var result = await manager.InstallPackAsync("hindi");

        result.Succeeded.Should().BeTrue(result.ErrorMessage);
        (await manager.GetInstalledPacksAsync()).Should().ContainSingle(p => p.Manifest.Id == "hindi");
        Directory.EnumerateFileSystemEntries(Path.Combine(_tempRoot, "RomanizationPacks"))
            .Should().ContainSingle().Which.Should().EndWith("hindi");
    }

    [Fact]
    public async Task InstallPackAsync_StalledBodyTimesOutAndRemovesPartialDownload()
    {
        var packBytes = CreateDevanagariPack();
        using var body = new StalledDownloadStream();
        using var handler = new TestHttpMessageHandler
        {
            SendAsyncFunc = (request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = request.RequestUri!.AbsoluteUri == RomanizationPackManager.DefaultCatalogUrl
                    ? new StringContent(CreateCatalogJson(CreateEntry("hindi", "devanagari-v1", packBytes)))
                    : new StreamContent(body)
            })
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(200) };
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(client);
        var verifier = Substitute.For<IRomanizationCatalogVerifier>();
        verifier.Verify(Arg.Any<RomanizationCatalogEnvelope>()).Returns(true);
        var manager = new RomanizationPackManager(factory, CreatePathConfiguration(),
            [new DevanagariRomanizationProvider()], verifier, NullLogger<RomanizationPackManager>.Instance);
        await manager.GetAvailablePacksAsync();

        var result = await manager.InstallPackAsync("hindi").WaitAsync(TimeSpan.FromSeconds(5));

        result.Succeeded.Should().BeFalse();
        body.TransferStarted.Should().BeTrue();
        Directory.EnumerateFileSystemEntries(Path.Combine(_tempRoot, "RomanizationPacks")).Should().BeEmpty();
    }

    [Fact]
    public async Task InstallPackAsync_ReplacementFailurePreservesInstalledPack()
    {
        var originalBytes = CreateDevanagariPack(("original.txt", "keep"));
        var installedPath = Path.Combine(_tempRoot, "RomanizationPacks", "hindi");
        ExtractArchive(originalBytes, installedPath);
        var replacement = CreateDevanagariPack(("replacement.txt", "new"));
        var provider = Substitute.For<IRomanizationProvider>();
        provider.EngineId.Returns("devanagari-v1");
        provider.ValidatePackAsync(Arg.Any<InstalledRomanizationPack>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                // Simulate the staging directory becoming unavailable before the final move.
                var stagedPath = call.Arg<InstalledRomanizationPack>().DirectoryPath;
                stagedPath.Should().StartWith(Path.Combine(_tempRoot, "RomanizationPacks") + Path.DirectorySeparatorChar);
                Directory.Delete(stagedPath, true);
                return true;
            });
        var manager = CreateManager(CreateCatalogJson(CreateEntry("hindi", "devanagari-v1", replacement)),
            replacement, validationProvider: provider);

        var result = await manager.InstallPackAsync("hindi");

        result.Succeeded.Should().BeFalse();
        File.ReadAllText(Path.Combine(installedPath, "original.txt")).Should().Be("keep");
        (await manager.GetInstalledPacksAsync()).Should().ContainSingle(p => p.Manifest.Id == "hindi");
        Directory.EnumerateFileSystemEntries(Path.GetDirectoryName(installedPath)!).Should().Equal(installedPath);
    }

    [Fact]
    public async Task InstallPackAsync_CancelledAfterValidationPreservesInstalledPack()
    {
        var packBytes = CreateDevanagariPack(("original.txt", "keep"));
        var installedPath = Path.Combine(_tempRoot, "RomanizationPacks", "hindi");
        ExtractArchive(packBytes, installedPath);
        using var cancellation = new CancellationTokenSource();
        var provider = Substitute.For<IRomanizationProvider>();
        provider.EngineId.Returns("devanagari-v1");
        provider.ValidatePackAsync(Arg.Any<InstalledRomanizationPack>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                cancellation.Cancel();
                return true;
            });
        var replacement = CreateDevanagariPack();
        var manager = CreateManager(CreateCatalogJson(CreateEntry("hindi", "devanagari-v1", replacement)),
            replacement, validationProvider: provider);

        var install = () => manager.InstallPackAsync("hindi", cancellation.Token);

        await install.Should().ThrowAsync<OperationCanceledException>();
        File.ReadAllText(Path.Combine(installedPath, "original.txt")).Should().Be("keep");
        Directory.EnumerateFileSystemEntries(Path.GetDirectoryName(installedPath)!).Should().Equal(installedPath);
    }

    [Fact]
    public async Task InstallPackAsync_WithHashMismatch_DoesNotInstall()
    {
        var packBytes = CreateDevanagariPack();
        var entry = CreateEntry("hindi", "devanagari-v1", packBytes);
        entry.Sha256 = "00";
        var manager = CreateManager(CreateCatalogJson(entry), packBytes);

        var result = await manager.InstallPackAsync("hindi");

        result.Succeeded.Should().BeFalse();
        result.ErrorMessage.Should().Contain("hash");
        (await manager.GetInstalledPacksAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task InstallPackAsync_WithInvalidCatalogSignature_Fails()
    {
        var packBytes = CreateDevanagariPack();
        var manager = CreateManager(CreateCatalogJson(CreateEntry("hindi", "devanagari-v1", packBytes)), packBytes, catalogIsValid: false);

        var result = await manager.InstallPackAsync("hindi");

        result.Succeeded.Should().BeFalse();
        result.ErrorMessage.Should().Contain("signature");
    }

    [Fact]
    public async Task InstallPackAsync_WithCorruptZip_DoesNotInstall()
    {
        var corruptBytes = Encoding.UTF8.GetBytes("not a zip archive");
        var manager = CreateManager(CreateCatalogJson(CreateEntry("hindi", "devanagari-v1", corruptBytes)), corruptBytes);

        var result = await manager.InstallPackAsync("hindi");

        result.Succeeded.Should().BeFalse();
        (await manager.GetInstalledPacksAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task InstallPackAsync_WithExecutableFile_DoesNotInstall()
    {
        var packBytes = CreateDevanagariPack(("tools/helper.exe", "not allowed"));
        var manager = CreateManager(CreateCatalogJson(CreateEntry("hindi", "devanagari-v1", packBytes)), packBytes);

        var result = await manager.InstallPackAsync("hindi");

        result.Succeeded.Should().BeFalse();
        result.ErrorMessage.Should().Contain("non-data");
        (await manager.GetInstalledPacksAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task GetAvailablePacksAsync_HidesUnsupportedEngines()
    {
        var packBytes = CreateDevanagariPack();
        var manager = CreateManager(CreateCatalogJson(CreateEntry("unknown", "unknown-v1", packBytes)), packBytes);

        var packs = await manager.GetAvailablePacksAsync(forceRefresh: true);

        packs.Should().BeEmpty();
    }

    [Theory]
    [InlineData("../sentinel.txt")]
    [InlineData("..\\sentinel.txt")]
    public async Task InstallPackAsync_WithTraversalEntry_PreservesFilesOutsideExtractionDirectory(string entryPath)
    {
        var packBytes = CreateDevanagariPack((entryPath, "overwrite"));
        var manager = CreateManager(CreateCatalogJson(CreateEntry("hindi", "devanagari-v1", packBytes)), packBytes);
        var packRoot = Directory.CreateDirectory(Path.Combine(_tempRoot, "RomanizationPacks")).FullName;
        var sentinel = Path.Combine(packRoot, "sentinel.txt");
        await File.WriteAllTextAsync(sentinel, "keep");

        var result = await manager.InstallPackAsync("hindi");

        result.Succeeded.Should().BeFalse();
        (await File.ReadAllTextAsync(sentinel)).Should().Be("keep");
        Directory.EnumerateFileSystemEntries(packRoot).Should().Equal(sentinel);
    }

    [Fact]
    public async Task GetInstalledPacksAsync_SkipsTemporaryExtractionDirectories()
    {
        var packRoot = Path.Combine(_tempRoot, "RomanizationPacks");
        ExtractArchive(CreateDevanagariPack(), Path.Combine(packRoot, "hindi"));
        ExtractArchive(CreateDevanagariPack(), Path.Combine(packRoot, $"{Guid.NewGuid():N}.tmp"));
        var manager = CreateManager(CreateCatalogJson(CreateEntry("hindi", "devanagari-v1", CreateDevanagariPack())), CreateDevanagariPack());

        var installed = await manager.GetInstalledPacksAsync();

        installed.Should().ContainSingle();
        installed[0].Manifest.Id.Should().Be("hindi");
        installed[0].DirectoryPath.Should().Be(Path.Combine(packRoot, "hindi"));
    }

    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData(".hidden")]
    [InlineData("hindi.")]
    [InlineData("../hindi")]
    [InlineData("..\\hindi")]
    [InlineData("C:\\Music")]
    [InlineData("hindi:stream")]
    public async Task RemovePackAsync_WithReservedPathPackId_DoesNotDeleteOutsideTargetPack(string packId)
    {
        var packBytes = CreateDevanagariPack();
        var manager = CreateManager(CreateCatalogJson(CreateEntry("hindi", "devanagari-v1", packBytes)), packBytes);
        var sentinelPath = Path.Combine(_tempRoot, "sentinel.txt");
        await File.WriteAllTextAsync(sentinelPath, "keep");

        var result = await manager.RemovePackAsync(packId);

        result.Succeeded.Should().BeFalse();
        File.Exists(sentinelPath).Should().BeTrue();
    }

    [Theory]
    [InlineData("japanese-hepburn")]
    [InlineData("hindi-devanagari")]
    [InlineData("korean-revised")]
    public async Task SourcePack_PassesBundledGoldenCases(string packId)
    {
        var pack = LoadSourcePack(packId);
        IRomanizationProvider[] providers =
            [new JapaneseRomanizationProvider(), new DevanagariRomanizationProvider(), new KoreanRomanizationProvider()];
        var provider = providers.Single(p => p.EngineId == pack.Manifest.EngineId);

        (await provider.ValidatePackAsync(pack)).Should().BeTrue();
    }
    [Fact]
    public async Task JapaneseProvider_WithOverlappingRules_SelectsLongestPhraseAndNormalizedSequence()
    {
        var rules = new
        {
            Phrases = new Dictionary<string, string>
            {
                ["愛"] = "ai", ["愛情歌"] = "aijouka", ["愛情"] = "aijou"
            },
            Sequences = new Dictionary<string, string> { ["き"] = "ki", ["きゃ"] = "kya" }
        };
        var manifest = new RomanizationPackManifest { Id = "japanese", EngineId = "japanese-v1" };
        var directory = Path.Combine(_tempRoot, "japanese");
        ExtractArchive(CreatePackArchive(manifest, "japanese-rules.json", rules,
            Array.Empty<RomanizationGoldenCase>(), Array.Empty<(string, string)>()), directory);
        var pack = new InstalledRomanizationPack { Manifest = manifest, DirectoryPath = directory };

        var result = await new JapaneseRomanizationProvider().RomanizeAsync("愛情歌 キャ 愛情 愛", pack);

        result.Should().Be("aijouka kya aijou ai");
    }

    [Fact]
    public void DistCatalog_IsSignedAndMatchesGeneratedPackHashes()
    {
        var distDirectory = Path.Combine(FindRepoRoot(), "tools", "romanization-packs", "dist");
        var catalogPath = Path.Combine(distDirectory, "catalog.json");
        var envelope = JsonSerializer.Deserialize<RomanizationCatalogEnvelope>(
            File.ReadAllText(catalogPath),
            RomanizationJson.Options)!;

        new EcdsaRomanizationCatalogVerifier().Verify(envelope).Should().BeTrue();

        foreach (var entry in envelope.Catalog.Packs)
        {
            var archiveName = Path.GetFileName(new Uri(entry.DownloadUrl).LocalPath);
            var archivePath = Path.Combine(distDirectory, archiveName);
            File.Exists(archivePath).Should().BeTrue();

            using var archive = File.OpenRead(archivePath);
            var actualHash = Convert.ToHexString(SHA256.HashData(archive));
            actualHash.Should().Be(entry.Sha256);
        }
    }

    [Fact]
    public async Task LyricRomanizationService_WhenDisabled_LeavesLyricsUnchanged()
    {
        var service = CreateLyricService(isEnabled: false, installedPack: CreateInstalledDevanagariPack());

        var lines = await service.ApplyRomanizationAsync(new[] { new LyricLine(TimeSpan.Zero, HindiText) });

        lines.Should().ContainSingle();
        lines[0].RomanizedText.Should().BeNull();
    }

    [Fact]
    public async Task LyricRomanizationService_WithMissingPack_LeavesSupportedLyricsUnchanged()
    {
        var service = CreateLyricService(isEnabled: true, installedPack: null);

        var lines = await service.ApplyRomanizationAsync(new[] { new LyricLine(TimeSpan.Zero, HindiText) });

        lines[0].RomanizedText.Should().BeNull();
    }

    [Fact]
    public async Task LyricRomanizationService_WithInstalledPack_RomanizesMixedScriptLine()
    {
        var service = CreateLyricService(isEnabled: true, installedPack: CreateInstalledDevanagariPack());

        var lines = await service.ApplyRomanizationAsync(new[] { new LyricLine(TimeSpan.Zero, $"hello {HindiText}!") });

        lines[0].Text.Should().Be($"hello {HindiText}!");
        lines[0].RomanizedText.Should().Be("hello hindi!");
    }

    [Fact]
    public async Task LyricRomanizationService_KeepsTranslationWithoutRomanizingIt()
    {
        var service = CreateLyricService(isEnabled: true, installedPack: CreateInstalledDevanagariPack());

        var lines = await service.ApplyRomanizationAsync(new[]
        {
            new LyricLine(TimeSpan.Zero, "hello") { TranslatedText = HindiText }
        });

        lines[0].TranslatedText.Should().Be(HindiText);
        lines[0].RomanizedText.Should().BeNull();
    }

    [Fact]
    public async Task LyricRomanizationService_WithInstalledKoreanPack_RomanizesMixedScriptLine()
    {
        var service = CreateLyricService(isEnabled: true, installedPack: LoadSourcePack("korean-revised"));

        var lines = await service.ApplyRomanizationAsync(new[] { new LyricLine(TimeSpan.Zero, $"sing {KoreanText}!") });

        lines[0].Text.Should().Be($"sing {KoreanText}!");
        lines[0].RomanizedText.Should().Be("sing saranghae!");
    }

    [Fact]
    public async Task LyricRomanizationService_WithMissingEarlierProviderPack_UsesInstalledLaterProvider()
    {
        var service = CreateLyricService(isEnabled: true, installedPack: LoadSourcePack("korean-revised"));

        var lines = await service.ApplyRomanizationAsync(new[] { new LyricLine(TimeSpan.Zero, $"世界 {KoreanText}") });

        lines[0].RomanizedText.Should().Be("世界 saranghae");
    }

    [Fact]
    public async Task LyricRomanizationService_WithMultipleInstalledPacks_RomanizesAllSupportedScripts()
    {
        var service = CreateLyricService(
            isEnabled: true,
            installedPack: CreateInstalledDevanagariPack(),
            LoadSourcePack("korean-revised"));

        var lines = await service.ApplyRomanizationAsync(new[] { new LyricLine(TimeSpan.Zero, $"{HindiText} {KoreanText}") });

        lines[0].RomanizedText.Should().Be("hindi saranghae");
    }

    [Fact]
    public async Task LyricRomanizationService_WithUnsupportedLine_LeavesLineUnchanged()
    {
        var service = CreateLyricService(isEnabled: true, installedPack: CreateInstalledDevanagariPack());

        var lines = await service.ApplyRomanizationAsync(new[] { new LyricLine(TimeSpan.Zero, "hello world") });

        lines[0].RomanizedText.Should().BeNull();
    }

    private static readonly string HindiText = "\u0939\u093F\u0928\u094D\u0926\u0940";
    private static readonly string KoreanText = "\uC0AC\uB791\uD574";

    private RomanizationPackManager CreateManager(string catalogJson, byte[] packBytes, bool catalogIsValid = true,
        long maxResponseContentBufferSize = int.MaxValue, IRomanizationProvider? validationProvider = null)
    {
        var responses = new Dictionary<string, byte[]>
        {
            [RomanizationPackManager.DefaultCatalogUrl] = Encoding.UTF8.GetBytes(catalogJson),
            ["https://packs.test/hindi.nagipack"] = packBytes,
            ["https://packs.test/unknown.nagipack"] = packBytes
        };
        var httpFactory = Substitute.For<IHttpClientFactory>();
        var client = new HttpClient(new TestHttpMessageHandler
        {
            SendAsyncFunc = (request, _) => Task.FromResult(
                responses.TryGetValue(request.RequestUri!.AbsoluteUri, out var bytes)
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }
                    : new HttpResponseMessage(HttpStatusCode.NotFound))
        })
        {
            MaxResponseContentBufferSize = maxResponseContentBufferSize
        };
        _clients.Add(client);
        httpFactory.CreateClient(Arg.Any<string>()).Returns(client);

        var pathConfig = CreatePathConfiguration();
        var verifier = Substitute.For<IRomanizationCatalogVerifier>();
        verifier.Verify(Arg.Any<RomanizationCatalogEnvelope>()).Returns(catalogIsValid);

        return new RomanizationPackManager(
            httpFactory,
            pathConfig,
            new IRomanizationProvider[] { validationProvider ?? new DevanagariRomanizationProvider(), new JapaneseRomanizationProvider(), new KoreanRomanizationProvider() },
            verifier,
            NullLogger<RomanizationPackManager>.Instance);
    }

    private ILyricRomanizationService CreateLyricService(
        bool isEnabled,
        InstalledRomanizationPack? installedPack,
        params InstalledRomanizationPack[] additionalInstalledPacks)
    {
        var settings = Substitute.For<ISettingsService>();
        settings.GetLyricsRomanizationEnabledAsync().Returns(Task.FromResult(isEnabled));

        var installedPacks = new List<InstalledRomanizationPack>();
        if (installedPack is not null) installedPacks.Add(installedPack);
        installedPacks.AddRange(additionalInstalledPacks);

        var packManager = Substitute.For<IRomanizationPackManager>();
        packManager.GetInstalledPacksAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<InstalledRomanizationPack>>(
                installedPacks));

        return new LyricRomanizationService(
            settings,
            packManager,
            new IRomanizationProvider[] { new DevanagariRomanizationProvider(), new JapaneseRomanizationProvider(), new KoreanRomanizationProvider() });
    }

    private InstalledRomanizationPack CreateInstalledDevanagariPack()
    {
        var packDirectory = Path.Combine(_tempRoot, Guid.NewGuid().ToString("N"));
        ExtractArchive(CreateDevanagariPack(), packDirectory);
        return new InstalledRomanizationPack
        {
            Manifest = new RomanizationPackManifest
            {
                Id = "hindi",
                DisplayName = "Hindi",
                Language = "Hindi",
                Script = "Devanagari",
                EngineId = "devanagari-v1",
                Version = "1.0.0"
            },
            DirectoryPath = packDirectory
        };
    }

    private static InstalledRomanizationPack LoadSourcePack(string packId)
    {
        var packDirectory = Path.Combine(FindRepoRoot(), "tools", "romanization-packs", "src", packId);
        var manifest = JsonSerializer.Deserialize<RomanizationPackManifest>(
            File.ReadAllText(Path.Combine(packDirectory, "manifest.json")),
            RomanizationJson.Options)!;

        return new InstalledRomanizationPack
        {
            Manifest = manifest,
            DirectoryPath = packDirectory
        };
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "tools", "romanization-packs", "src")))
                return directory.FullName;

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not find repository root.");
    }

    private static string CreateCatalogJson(RomanizationPackCatalogEntry entry)
    {
        var envelope = new RomanizationCatalogEnvelope
        {
            Catalog = new RomanizationPackCatalog
            {
                GeneratedAt = DateTimeOffset.UtcNow,
                Packs = new List<RomanizationPackCatalogEntry> { entry }
            },
            Signature = "test-signature"
        };

        return JsonSerializer.Serialize(envelope, RomanizationJson.Options);
    }

    private static RomanizationPackCatalogEntry CreateEntry(string id, string engineId, byte[] bytes)
    {
        return new RomanizationPackCatalogEntry
        {
            Id = id,
            DisplayName = id,
            Language = id,
            Script = "Devanagari",
            EngineId = engineId,
            Version = "1.0.0",
            MinAppVersion = "0.0.0.0",
            Description = "Test pack",
            License = "Test",
            Attribution = "Test",
            SizeBytes = bytes.Length,
            Sha256 = Convert.ToHexString(SHA256.HashData(bytes)),
            DownloadUrl = $"https://packs.test/{id}.nagipack"
        };
    }

    private static byte[] CreateDevanagariPack(params (string Path, string Content)[] extraEntries)
    {
        var manifest = new RomanizationPackManifest
        {
            Id = "hindi",
            DisplayName = "Hindi",
            Language = "Hindi",
            Script = "Devanagari",
            EngineId = "devanagari-v1",
            Version = "1.0.0",
            License = "Test",
            Attribution = "Test"
        };

        var rules = new
        {
            Phrases = new Dictionary<string, string> { [HindiText] = "hindi" },
        };

        var golden = new[] { new RomanizationGoldenCase { Input = HindiText, Expected = "hindi" } };
        return CreatePackArchive(manifest, "devanagari-rules.json", rules, golden, extraEntries);
    }

    private static byte[] CreatePackArchive(
        RomanizationPackManifest manifest,
        string rulesFileName,
        object rules,
        IReadOnlyList<RomanizationGoldenCase> golden,
        IReadOnlyList<(string Path, string Content)> extraEntries)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteJsonEntry(archive, "manifest.json", manifest);
            WriteJsonEntry(archive, $"data/{rulesFileName}", rules);
            WriteJsonEntry(archive, "tests/golden.json", golden);
            WriteTextEntry(archive, "NOTICE.txt", "Test attribution");
            foreach (var (path, content) in extraEntries)
                WriteTextEntry(archive, path, content);
        }

        return stream.ToArray();
    }

    private static void WriteJsonEntry(ZipArchive archive, string path, object value)
    {
        WriteTextEntry(archive, path, JsonSerializer.Serialize(value, RomanizationJson.Options));
    }

    private static void WriteTextEntry(ZipArchive archive, string path, string value)
    {
        var entry = archive.CreateEntry(path);
        using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
        writer.Write(value);
    }

    private static void ExtractArchive(byte[] archiveBytes, string destination)
    {
        using var stream = new MemoryStream(archiveBytes);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        archive.ExtractToDirectory(destination);
    }

    private sealed class StalledDownloadStream : MemoryStream
    {
        public bool TransferStarted { get; private set; }

        public override async Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
        {
            TransferStarted = true;
            await destination.WriteAsync(new byte[] { 1, 2, 3 }, cancellationToken);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private IPathConfiguration CreatePathConfiguration()
    {
        var paths = Substitute.For<IPathConfiguration>();
        paths.RomanizationPacksPath.Returns(Path.Combine(_tempRoot, "RomanizationPacks"));
        return paths;
    }
}
