using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Nagi.Core.Models.Romanization;

var jsonOptions = new JsonSerializerOptions
{
    PropertyNameCaseInsensitive = true
};

if (args.Length > 0 && args[0].Equals("generate-key", StringComparison.OrdinalIgnoreCase))
{
    var keyDirectory = args.Length > 1 ? args[1] : Path.Combine("tools", "romanization-packs", ".signing");
    GenerateSigningKey(keyDirectory);
    return;
}

var options = PackToolOptions.Parse(args);
var privateKeyPem = Environment.GetEnvironmentVariable("NAGI_ROMANIZATION_CATALOG_PRIVATE_KEY_PEM");
if (string.IsNullOrWhiteSpace(privateKeyPem) && File.Exists(Path.Combine(options.OutputDirectory, "catalog.json")))
    throw new InvalidOperationException("The output contains a signed catalog. Set NAGI_ROMANIZATION_CATALOG_PRIVATE_KEY_PEM or choose a different --output directory before rebuilding its packs.");

Directory.CreateDirectory(options.OutputDirectory);

var entries = new List<RomanizationPackCatalogEntry>();
foreach (var packDirectory in Directory.EnumerateDirectories(options.SourceDirectory).Order(StringComparer.OrdinalIgnoreCase))
{
    var manifestPath = Path.Combine(packDirectory, "manifest.json");
    if (!File.Exists(manifestPath)) continue;

    await using var manifestStream = File.OpenRead(manifestPath);
    var manifest = await JsonSerializer.DeserializeAsync<RomanizationPackManifest>(
        manifestStream,
        jsonOptions) ?? throw new InvalidOperationException($"Invalid manifest: {manifestPath}");

    var metadata = await ReadMetadataAsync(packDirectory, jsonOptions);
    var archiveName = $"{manifest.Id}-{manifest.Version}.nagipack";
    var archivePath = Path.Combine(options.OutputDirectory, archiveName);

    CreatePackArchive(packDirectory, archivePath);
    await using var archiveStream = File.OpenRead(archivePath);
    var archiveHash = await SHA256.HashDataAsync(archiveStream);

    entries.Add(new RomanizationPackCatalogEntry
    {
        Id = manifest.Id,
        DisplayName = manifest.DisplayName,
        Language = manifest.Language,
        Script = manifest.Script,
        EngineId = manifest.EngineId,
        Version = manifest.Version,
        MinAppVersion = metadata.MinAppVersion,
        Description = metadata.Description,
        License = manifest.License,
        Attribution = manifest.Attribution,
        SizeBytes = archiveStream.Length,
        Sha256 = Convert.ToHexString(archiveHash),
        DownloadUrl = $"{options.BaseDownloadUrl.TrimEnd('/')}/{Uri.EscapeDataString(archiveName)}"
    });

    Console.WriteLine($"Packed {manifest.Id} -> {archivePath}");
}

var catalog = new RomanizationPackCatalog
{
    Version = 1,
    GeneratedAt = DateTimeOffset.UtcNow,
    Packs = entries.OrderBy(e => e.DisplayName, StringComparer.OrdinalIgnoreCase).ToList()
};

var unsignedEnvelope = new RomanizationCatalogEnvelope
{
    Catalog = catalog,
    Signature = string.Empty
};

var unsignedPath = Path.Combine(options.OutputDirectory, "catalog.unsigned.json");
await File.WriteAllTextAsync(unsignedPath, JsonSerializer.Serialize(unsignedEnvelope, jsonOptions));
Console.WriteLine($"Wrote unsigned catalog -> {unsignedPath}");

if (!string.IsNullOrWhiteSpace(privateKeyPem))
{
    privateKeyPem = privateKeyPem.Replace("\\n", Environment.NewLine, StringComparison.Ordinal);
    var signedEnvelope = new RomanizationCatalogEnvelope
    {
        Catalog = catalog,
        Signature = SignCatalog(catalog, privateKeyPem, jsonOptions)
    };

    var signedPath = Path.Combine(options.OutputDirectory, "catalog.json");
    await File.WriteAllTextAsync(signedPath, JsonSerializer.Serialize(signedEnvelope, jsonOptions));
    Console.WriteLine($"Wrote signed catalog -> {signedPath}");
}
else
{
    Console.WriteLine("Set NAGI_ROMANIZATION_CATALOG_PRIVATE_KEY_PEM to emit signed catalog.json.");
}

static async Task<CatalogEntryMetadata> ReadMetadataAsync(string packDirectory, JsonSerializerOptions jsonOptions)
{
    var metadataPath = Path.Combine(packDirectory, "catalog-entry.json");
    if (!File.Exists(metadataPath)) return new CatalogEntryMetadata();

    await using var stream = File.OpenRead(metadataPath);
    return await JsonSerializer.DeserializeAsync<CatalogEntryMetadata>(
        stream,
        jsonOptions) ?? new CatalogEntryMetadata();
}

static void CreatePackArchive(string sourceDirectory, string archivePath)
{
    using var fileStream = File.Create(archivePath);
    using var archive = new ZipArchive(fileStream, ZipArchiveMode.Create);

    foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories)
                 .Order(StringComparer.OrdinalIgnoreCase))
    {
        var relativePath = Path.GetRelativePath(sourceDirectory, file).Replace('\\', '/');
        if (relativePath.Equals("catalog-entry.json", StringComparison.OrdinalIgnoreCase)) continue;

        archive.CreateEntryFromFile(file, relativePath, CompressionLevel.Optimal);
    }
}

static string SignCatalog(RomanizationPackCatalog catalog, string privateKeyPem, JsonSerializerOptions jsonOptions)
{
    var payload = JsonSerializer.SerializeToUtf8Bytes(catalog, jsonOptions);
    using var ecdsa = ECDsa.Create();
    ecdsa.ImportFromPem(privateKeyPem);
    return Convert.ToBase64String(ecdsa.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));
}

static void GenerateSigningKey(string keyDirectory)
{
    Directory.CreateDirectory(keyDirectory);
    using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    var privateKeyPath = Path.Combine(keyDirectory, "catalog-private-key.pem");
    var publicKeyPath = Path.Combine(keyDirectory, "catalog-public-key.pem");

    File.WriteAllText(privateKeyPath, ecdsa.ExportPkcs8PrivateKeyPem());
    File.WriteAllText(publicKeyPath, ecdsa.ExportSubjectPublicKeyInfoPem());

    Console.WriteLine($"Wrote private key -> {privateKeyPath}");
    Console.WriteLine($"Wrote public key -> {publicKeyPath}");
    Console.WriteLine("Keep the private key out of git and move it to a release secret before publishing packs.");
}

internal sealed class CatalogEntryMetadata
{
    public string Description { get; set; } = string.Empty;
    public string MinAppVersion { get; set; } = "0.0.0.0";
}

internal sealed record PackToolOptions
{
    public string SourceDirectory { get; private init; } = Path.Combine("tools", "romanization-packs", "src");
    public string OutputDirectory { get; private init; } = Path.Combine("tools", "romanization-packs", "dist");
    public string BaseDownloadUrl { get; private init; } = "https://github.com/Anthonyy232/Nagi/releases/download/romanization-packs";

    public static PackToolOptions Parse(string[] args)
    {
        var options = new PackToolOptions();
        for (var i = 0; i < args.Length; i++)
        {
            var next = i + 1 < args.Length ? args[i + 1] : null;
            switch (args[i])
            {
                case "--source" when next is not null:
                    options = options with { SourceDirectory = next };
                    i++;
                    break;
                case "--output" when next is not null:
                    options = options with { OutputDirectory = next };
                    i++;
                    break;
                case "--base-url" when next is not null:
                    options = options with { BaseDownloadUrl = next };
                    i++;
                    break;
                default:
                    throw new ArgumentException($"Unknown or incomplete argument: {args[i]}");
            }
        }

        return options;
    }
}
