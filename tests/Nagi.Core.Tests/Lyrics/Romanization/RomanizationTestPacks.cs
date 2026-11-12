using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Nagi.Core.Lyrics.Romanization;

namespace Nagi.Core.Tests.Lyrics.Romanization;

internal static class RomanizationTestPacks
{
    public static readonly string HindiText = "\u0939\u093F\u0928\u094D\u0926\u0940";

    public static InstalledRomanizationPack LoadSourcePack(string packId)
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

    public static string FindRepoRoot()
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

    public static byte[] CreateDevanagariPack(params (string Path, string Content)[] extraEntries)
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

    public static byte[] CreatePackArchive(
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

    public static void WriteJsonEntry(ZipArchive archive, string path, object value)
    {
        WriteTextEntry(archive, path, JsonSerializer.Serialize(value, RomanizationJson.Options));
    }

    public static void WriteTextEntry(ZipArchive archive, string path, string value)
    {
        var entry = archive.CreateEntry(path);
        using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
        writer.Write(value);
    }

    public static void ExtractArchive(byte[] archiveBytes, string destination)
    {
        using var stream = new MemoryStream(archiveBytes);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        archive.ExtractToDirectory(destination);
    }
}
