using AwesomeAssertions;
using Nagi.Core.Lyrics;
using Nagi.Core.Lyrics.Romanization;
using Nagi.Core.Platform;
using Nagi.Core.Settings;
using Nagi.Core.Tests.Utils;
using NSubstitute;
using Xunit;
using static Nagi.Core.Tests.Lyrics.Romanization.RomanizationTestPacks;

namespace Nagi.Core.Tests.Lyrics.Romanization;

public sealed class LyricRomanizationServiceTests : IDisposable
{
    private static readonly string KoreanText = "\uC0AC\uB791\uD574";

    private readonly string _tempRoot = Directory.CreateTempSubdirectory("NagiRomanizationTests-").FullName;

    public void Dispose() => Directory.Delete(_tempRoot, true);

    [Fact]
    public async Task WhenDisabled_LeavesLyricsUnchanged()
    {
        var service = CreateLyricService(isEnabled: false, installedPack: CreateInstalledDevanagariPack());

        var lines = await service.ApplyRomanizationAsync(new[] { new LyricLine(TimeSpan.Zero, HindiText) });

        lines.Should().ContainSingle();
        lines[0].RomanizedText.Should().BeNull();
    }

    [Fact]
    public async Task ReadsAPacksRulesOnceAcrossLyrics()
    {
        var pack = CreateInstalledDevanagariPack();
        var fileSystem = Substitute.For<IFileSystemService>();
        fileSystem.ReadAllTextAsync(Arg.Any<string>()).Returns(call => File.ReadAllTextAsync(call.Arg<string>()));
        var settings = TestSettings.Create();
        settings.Set(CoreSettings.LyricsRomanization, true);
        var packManager = Substitute.For<IRomanizationPackManager>();
        packManager.GetInstalledPacksAsync(Arg.Any<CancellationToken>()).Returns([pack]);
        var service = new LyricRomanizationService(settings, packManager, [new DevanagariRomanizationProvider()], fileSystem);

        var first = await service.ApplyRomanizationAsync([new LyricLine(TimeSpan.Zero, HindiText)]);
        var second = await service.ApplyRomanizationAsync([new LyricLine(TimeSpan.Zero, $"{HindiText}!")]);

        first[0].RomanizedText.Should().Be("hindi");
        second[0].RomanizedText.Should().Be("hindi!");
        await fileSystem.Received(1).ReadAllTextAsync(Arg.Any<string>());
    }

    [Fact]
    public async Task WithMissingPack_LeavesSupportedLyricsUnchanged()
    {
        var service = CreateLyricService(isEnabled: true, installedPack: null);

        var lines = await service.ApplyRomanizationAsync(new[] { new LyricLine(TimeSpan.Zero, HindiText) });

        lines[0].RomanizedText.Should().BeNull();
    }

    [Fact]
    public async Task WithInstalledPack_RomanizesMixedScriptLine()
    {
        var service = CreateLyricService(isEnabled: true, installedPack: CreateInstalledDevanagariPack());

        var lines = await service.ApplyRomanizationAsync(new[] { new LyricLine(TimeSpan.Zero, $"hello {HindiText}!") });

        lines[0].Text.Should().Be($"hello {HindiText}!");
        lines[0].RomanizedText.Should().Be("hello hindi!");
    }

    [Fact]
    public async Task KeepsTranslationWithoutRomanizingIt()
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
    public async Task WithMissingEarlierProviderPack_UsesInstalledLaterProvider()
    {
        var service = CreateLyricService(isEnabled: true, installedPack: LoadSourcePack("korean-revised"));

        var lines = await service.ApplyRomanizationAsync(new[] { new LyricLine(TimeSpan.Zero, $"世界 {KoreanText}") });

        lines[0].RomanizedText.Should().Be("世界 saranghae");
    }

    [Fact]
    public async Task WithMultipleInstalledPacks_RomanizesAllSupportedScripts()
    {
        var service = CreateLyricService(
            isEnabled: true,
            installedPack: CreateInstalledDevanagariPack(),
            LoadSourcePack("korean-revised"));

        var lines = await service.ApplyRomanizationAsync(new[] { new LyricLine(TimeSpan.Zero, $"{HindiText} {KoreanText}") });

        lines[0].RomanizedText.Should().Be("hindi saranghae");
    }

    [Fact]
    public async Task WithUnsupportedLine_LeavesLineUnchanged()
    {
        var service = CreateLyricService(isEnabled: true, installedPack: CreateInstalledDevanagariPack());

        var lines = await service.ApplyRomanizationAsync(new[] { new LyricLine(TimeSpan.Zero, "hello world") });

        lines[0].RomanizedText.Should().BeNull();
    }

    private static ILyricRomanizationService CreateLyricService(
        bool isEnabled,
        InstalledRomanizationPack? installedPack,
        params InstalledRomanizationPack[] additionalInstalledPacks)
    {
        var settings = TestSettings.Create();
        settings.Set(CoreSettings.LyricsRomanization, isEnabled);

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
            new IRomanizationProvider[] { new DevanagariRomanizationProvider(), new JapaneseRomanizationProvider(), new KoreanRomanizationProvider() },
            new FileSystemService());
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
}
