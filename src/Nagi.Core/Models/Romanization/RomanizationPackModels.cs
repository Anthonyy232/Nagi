namespace Nagi.Core.Models.Romanization;

public sealed class RomanizationCatalogEnvelope
{
    public RomanizationPackCatalog Catalog { get; set; } = new();
    public string Signature { get; set; } = string.Empty;
}

public sealed class RomanizationPackCatalog
{
    public int Version { get; set; } = 1;
    public DateTimeOffset GeneratedAt { get; set; } = DateTimeOffset.MinValue;
    public List<RomanizationPackCatalogEntry> Packs { get; set; } = new();
}

public sealed class RomanizationPackCatalogEntry
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Language { get; set; } = string.Empty;
    public string Script { get; set; } = string.Empty;
    public string EngineId { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string MinAppVersion { get; set; } = "0.0.0.0";
    public string Description { get; set; } = string.Empty;
    public string License { get; set; } = string.Empty;
    public string Attribution { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public string DownloadUrl { get; set; } = string.Empty;
}

public sealed class RomanizationPackManifest
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Language { get; set; } = string.Empty;
    public string Script { get; set; } = string.Empty;
    public string EngineId { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string License { get; set; } = string.Empty;
    public string Attribution { get; set; } = string.Empty;
}

public sealed class RomanizationGoldenCase
{
    public string Input { get; set; } = string.Empty;
    public string Expected { get; set; } = string.Empty;
}

public sealed class InstalledRomanizationPack
{
    public RomanizationPackManifest Manifest { get; set; } = new();
    public string DirectoryPath { get; set; } = string.Empty;
}

public sealed class RomanizationPackView
{
    public RomanizationPackCatalogEntry CatalogEntry { get; set; } = new();
    public InstalledRomanizationPack? InstalledPack { get; set; }
    public bool IsInstalled => InstalledPack is not null;
    public bool IsUpdateAvailable => InstalledPack is not null &&
                                     RomanizationVersionComparer.CompareVersions(
                                         CatalogEntry.Version,
                                         InstalledPack.Manifest.Version) > 0;
}

public sealed class RomanizationPackOperationResult
{
    private RomanizationPackOperationResult(bool succeeded, string? errorMessage = null)
    {
        Succeeded = succeeded;
        ErrorMessage = errorMessage;
    }

    public bool Succeeded { get; }
    public string? ErrorMessage { get; }

    public static RomanizationPackOperationResult Success() => new(true);
    public static RomanizationPackOperationResult Failure(string message) => new(false, message);
}

public static class RomanizationVersionComparer
{
    public static int CompareVersions(string? left, string? right)
    {
        if (Version.TryParse(left, out var leftVersion) && Version.TryParse(right, out var rightVersion))
            return leftVersion.CompareTo(rightVersion);

        return string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
    }
}
