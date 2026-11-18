using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nagi.Core.Lyrics.Romanization;
using Nagi.Core.Platform;
using Nagi.Core.Tests.Utils;
using NSubstitute;
using Xunit;
using static Nagi.Core.Tests.Lyrics.Romanization.RomanizationTestPacks;

namespace Nagi.Core.Tests.Lyrics.Romanization;

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
                Content = request.RequestUri!.AbsoluteUri == RomanizationPackManager.CatalogUrl
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
            [new DevanagariRomanizationProvider()], verifier, new FileSystemService(), AppInfo(),
            NullLogger<RomanizationPackManager>.Instance);
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
        // Simulate the staging directory becoming unavailable before the final move.
        var provider = DevanagariProviderThat(beforeValidation: () =>
            Directory.Delete(Directory.EnumerateDirectories(Path.GetDirectoryName(installedPath)!, "*.tmp").Single(), true));
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
        var provider = DevanagariProviderThat(beforeValidation: cancellation.Cancel);
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

    // The app reports three version parts and a catalog may give four.
    [Theory]
    [InlineData("2.7.0.0", true)]
    [InlineData("2.8.0", false)]
    public async Task GetAvailablePacksAsync_OffersOnlyPacksThisAppVersionCanUse(string minAppVersion, bool offered)
    {
        var packBytes = CreateDevanagariPack();
        var entry = CreateEntry("hindi", "devanagari-v1", packBytes);
        entry.MinAppVersion = minAppVersion;
        var manager = CreateManager(CreateCatalogJson(entry), packBytes);

        var packs = await manager.GetAvailablePacksAsync();

        packs.Should().HaveCount(offered ? 1 : 0);
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

    [Fact]
    public async Task GetInstalledPacksAsync_ReadsTheDiskAgainOnlyAfterAPackChanges()
    {
        var packRoot = Path.Combine(_tempRoot, "RomanizationPacks");
        var packBytes = CreateDevanagariPack();
        var manager = CreateManager(CreateCatalogJson(CreateEntry("hindi", "devanagari-v1", packBytes)), packBytes);
        (await manager.GetInstalledPacksAsync()).Should().BeEmpty();
        ExtractArchive(packBytes, Path.Combine(packRoot, "hindi"));

        (await manager.GetInstalledPacksAsync()).Should().BeEmpty();
        (await manager.InstallPackAsync("hindi")).Succeeded.Should().BeTrue();
        (await manager.GetInstalledPacksAsync()).Should().ContainSingle();
        await manager.RemovePackAsync("hindi");
        (await manager.GetInstalledPacksAsync()).Should().BeEmpty();
    }

    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData(".hidden")]
    [InlineData("hindi.")]
    [InlineData("../hindi")]
    [InlineData("..\\hindi")]
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

    [Fact]
    public async Task RemovePackAsync_WithRootedPackId_DoesNotDeleteThatDirectory()
    {
        var packBytes = CreateDevanagariPack();
        var manager = CreateManager(CreateCatalogJson(CreateEntry("hindi", "devanagari-v1", packBytes)), packBytes);
        var rootedDirectory = Directory.CreateDirectory(Path.Combine(_tempRoot, "Music")).FullName;
        var sentinelPath = Path.Combine(rootedDirectory, "sentinel.txt");
        await File.WriteAllTextAsync(sentinelPath, "keep");

        var result = await manager.RemovePackAsync(rootedDirectory);

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

        (await RomanizationPackFiles.ValidateAsync(new FileSystemService(), provider, pack, CancellationToken.None))
            .Should().BeTrue();
    }
    [Fact]
    public void JapaneseProvider_WithOverlappingRules_SelectsLongestPhraseAndNormalizedSequence()
    {
        var rules = new
        {
            Phrases = new Dictionary<string, string>
            {
                ["愛"] = "ai",
                ["愛情歌"] = "aijouka",
                ["愛情"] = "aijou"
            },
            Sequences = new Dictionary<string, string> { ["き"] = "ki", ["きゃ"] = "kya" }
        };
        var provider = new JapaneseRomanizationProvider();

        var result = provider.Romanize("愛情歌 キャ 愛情 愛", provider.ParseRules(JsonSerializer.Serialize(rules)));

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

        envelope.Catalog.Packs[0].Sha256 = new string('0', 64);
        new EcdsaRomanizationCatalogVerifier().Verify(envelope).Should().BeFalse("the signature covers the pack hashes");
    }

    private RomanizationPackManager CreateManager(string catalogJson, byte[] packBytes, bool catalogIsValid = true,
        long maxResponseContentBufferSize = int.MaxValue, IRomanizationProvider? validationProvider = null)
    {
        var responses = new Dictionary<string, byte[]>
        {
            [RomanizationPackManager.CatalogUrl] = Encoding.UTF8.GetBytes(catalogJson),
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
            new FileSystemService(),
            AppInfo(),
            NullLogger<RomanizationPackManager>.Instance);
    }

    private static IAppInfoService AppInfo()
    {
        var appInfo = Substitute.For<IAppInfoService>();
        appInfo.GetAppVersion().Returns("2.7.0");
        return appInfo;
    }

    // A real Devanagari provider that runs an action when a pack's rules are read for validation.
    private static IRomanizationProvider DevanagariProviderThat(Action beforeValidation)
    {
        var real = new DevanagariRomanizationProvider();
        var provider = Substitute.For<IRomanizationProvider>();
        provider.EngineId.Returns(real.EngineId);
        provider.RulesFileName.Returns(real.RulesFileName);
        provider.Supports(Arg.Any<string>()).Returns(call => real.Supports(call.Arg<string>()));
        provider.Romanize(Arg.Any<string>(), Arg.Any<object>())
            .Returns(call => real.Romanize(call.Arg<string>(), call.Arg<object>()));
        provider.ParseRules(Arg.Any<string>()).Returns(call =>
        {
            beforeValidation();
            return real.ParseRules(call.Arg<string>());
        });
        return provider;
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
