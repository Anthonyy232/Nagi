using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Nagi.Core.Helpers;
using Nagi.Core.Models.Romanization;
using Nagi.Core.Services.Abstractions;

namespace Nagi.Core.Services.Implementations.Romanization;

public sealed class RomanizationPackManager : IRomanizationPackManager
{
    public const string DefaultCatalogUrl = "https://github.com/Anthonyy232/Nagi/releases/download/romanization-packs/catalog.json";
    private static readonly HashSet<string> AllowedPackExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".csv",
        ".dic",
        ".dict",
        ".json",
        ".md",
        ".tsv",
        ".txt"
    };

    private readonly HttpClient _httpClient;
    private readonly ILogger<RomanizationPackManager> _logger;
    private readonly IPathConfiguration _pathConfig;
    private readonly Dictionary<string, IRomanizationProvider> _providers;
    private readonly IRomanizationCatalogVerifier _catalogVerifier;
    private RomanizationPackCatalog? _cachedCatalog;

    public event Action? PacksChanged;

    public RomanizationPackManager(
        IHttpClientFactory httpClientFactory,
        IPathConfiguration pathConfig,
        IEnumerable<IRomanizationProvider> providers,
        IRomanizationCatalogVerifier catalogVerifier,
        ILogger<RomanizationPackManager> logger)
    {
        _httpClient = httpClientFactory.CreateClient();
        _pathConfig = pathConfig;
        _providers = providers.ToDictionary(p => p.EngineId, StringComparer.OrdinalIgnoreCase);
        _catalogVerifier = catalogVerifier;
        _logger = logger;
    }

    public async Task<IReadOnlyList<RomanizationPackView>> GetAvailablePacksAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        var catalog = await GetCatalogAsync(forceRefresh, cancellationToken).ConfigureAwait(false);
        var installed = await GetInstalledPacksAsync(cancellationToken).ConfigureAwait(false);
        var installedById = installed.ToDictionary(p => p.Manifest.Id, StringComparer.OrdinalIgnoreCase);
        var appVersion = Assembly.GetEntryAssembly()?.GetName().Version ?? typeof(RomanizationPackManager).Assembly.GetName().Version ?? new Version(0, 0);

        return catalog.Packs
            .Where(p => _providers.ContainsKey(p.EngineId))
            .Where(p => IsCompatibleWithAppVersion(p.MinAppVersion, appVersion))
            .Select(p => new RomanizationPackView
            {
                CatalogEntry = p,
                InstalledPack = installedById.TryGetValue(p.Id, out var installedPack) ? installedPack : null
            })
            .OrderBy(p => p.CatalogEntry.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public Task<IReadOnlyList<InstalledRomanizationPack>> GetInstalledPacksAsync(CancellationToken cancellationToken = default)
    {
        EnsurePackRoot();
        var packs = new List<InstalledRomanizationPack>();

        foreach (var directory in Directory.EnumerateDirectories(PackRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directoryInfo = new DirectoryInfo(directory);
            if (directoryInfo.Name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;

            var manifestPath = Path.Combine(directory, "manifest.json");
            if (!File.Exists(manifestPath)) continue;

            try
            {
                var manifest = JsonSerializer.Deserialize<RomanizationPackManifest>(
                    File.ReadAllText(manifestPath),
                    RomanizationJson.Options);

                if (manifest is null || string.IsNullOrWhiteSpace(manifest.Id)) continue;
                if (!string.Equals(directoryInfo.Name, SanitizePackId(manifest.Id), StringComparison.OrdinalIgnoreCase)) continue;

                packs.Add(new InstalledRomanizationPack { Manifest = manifest, DirectoryPath = directory });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Skipping invalid romanization pack manifest at {Path}", manifestPath);
            }
        }

        return Task.FromResult<IReadOnlyList<InstalledRomanizationPack>>(packs);
    }

    public async Task<RomanizationPackOperationResult> InstallPackAsync(string packId, CancellationToken cancellationToken = default)
    {
        try
        {
            var pack = (await GetAvailablePacksAsync(false, cancellationToken).ConfigureAwait(false))
                .FirstOrDefault(p => string.Equals(p.CatalogEntry.Id, packId, StringComparison.OrdinalIgnoreCase))
                ?.CatalogEntry;

            if (pack is null) return RomanizationPackOperationResult.Failure("Pack is not available for this app version.");
            if (!_providers.TryGetValue(pack.EngineId, out var provider)) return RomanizationPackOperationResult.Failure("Pack engine is not supported.");
            if (!Uri.TryCreate(pack.DownloadUrl, UriKind.Absolute, out var downloadUri) || downloadUri.Scheme != Uri.UriSchemeHttps)
                return RomanizationPackOperationResult.Failure("Pack download URL must use HTTPS.");

            EnsurePackRoot();
            var tempArchivePath = Path.Combine(PackRoot, $"{Guid.NewGuid():N}.nagipack");
            var tempExtractPath = Path.Combine(PackRoot, $"{Guid.NewGuid():N}.tmp");

            try
            {
                var bytes = await _httpClient.GetByteArrayAsync(downloadUri, cancellationToken).ConfigureAwait(false);
                if (!MatchesSha256(bytes, pack.Sha256))
                    return RomanizationPackOperationResult.Failure("Downloaded pack hash did not match the signed catalog.");

                await File.WriteAllBytesAsync(tempArchivePath, bytes, cancellationToken).ConfigureAwait(false);
                ExtractZipSafely(tempArchivePath, tempExtractPath);

                var manifestPath = Path.Combine(tempExtractPath, "manifest.json");
                if (!File.Exists(manifestPath)) return RomanizationPackOperationResult.Failure("Pack does not contain a manifest.");

                var manifest = JsonSerializer.Deserialize<RomanizationPackManifest>(
                    await File.ReadAllTextAsync(manifestPath, cancellationToken).ConfigureAwait(false),
                    RomanizationJson.Options);

                if (manifest is null) return RomanizationPackOperationResult.Failure("Pack manifest could not be read.");
                if (!MatchesCatalogEntry(pack, manifest)) return RomanizationPackOperationResult.Failure("Pack manifest does not match the signed catalog.");

                var installedPack = new InstalledRomanizationPack { Manifest = manifest, DirectoryPath = tempExtractPath };
                if (!await provider.ValidatePackAsync(installedPack, cancellationToken).ConfigureAwait(false))
                    return RomanizationPackOperationResult.Failure("Pack self-test failed.");

                var finalPath = GetPackDirectory(pack.Id);
                if (Directory.Exists(finalPath)) Directory.Delete(finalPath, true);
                Directory.Move(tempExtractPath, finalPath);
                PacksChanged?.Invoke();
                return RomanizationPackOperationResult.Success();
            }
            finally
            {
                TryDeleteFile(tempArchivePath);
                TryDeleteDirectory(tempExtractPath);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to install romanization pack {PackId}", packId);
            return RomanizationPackOperationResult.Failure(ex.Message);
        }
    }

    public Task<RomanizationPackOperationResult> RemovePackAsync(string packId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var path = GetPackDirectory(packId);
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
                PacksChanged?.Invoke();
            }
            return Task.FromResult(RomanizationPackOperationResult.Success());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to remove romanization pack {PackId}", packId);
            return Task.FromResult(RomanizationPackOperationResult.Failure(ex.Message));
        }
    }

    private async Task<RomanizationPackCatalog> GetCatalogAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        if (!forceRefresh && _cachedCatalog is not null) return _cachedCatalog;

        var json = await _httpClient.GetStringAsync(DefaultCatalogUrl, cancellationToken).ConfigureAwait(false);
        var envelope = JsonSerializer.Deserialize<RomanizationCatalogEnvelope>(json, RomanizationJson.Options)
                       ?? throw new InvalidOperationException("Catalog response was empty.");

        if (!_catalogVerifier.Verify(envelope))
            throw new InvalidOperationException("Romanization pack catalog signature is invalid.");

        _cachedCatalog = envelope.Catalog;
        return _cachedCatalog;
    }

    private string PackRoot => _pathConfig.RomanizationPacksPath;

    private void EnsurePackRoot()
    {
        Directory.CreateDirectory(PackRoot);
    }

    private string GetPackDirectory(string packId)
    {
        var packRoot = Path.GetFullPath(PackRoot);
        var packPath = Path.GetFullPath(Path.Combine(packRoot, SanitizePackId(packId)));
        if (!IsPathInsideDirectory(packRoot, packPath))
            throw new ArgumentException("Pack ID resolves outside the pack directory.", nameof(packId));

        return packPath;
    }

    private static string SanitizePackId(string packId)
    {
        if (string.IsNullOrWhiteSpace(packId)) throw new ArgumentException("Pack ID is required.", nameof(packId));

        packId = packId.Trim();
        if (packId is "." or ".."
            || packId.StartsWith(".", StringComparison.Ordinal)
            || packId.EndsWith(".", StringComparison.Ordinal)
            || packId.Contains("..", StringComparison.Ordinal))
            throw new ArgumentException("Pack ID is invalid.", nameof(packId));

        if (packId.Any(c => !char.IsLetterOrDigit(c) && c is not '-' and not '_' and not '.'))
            throw new ArgumentException("Pack ID contains invalid characters.", nameof(packId));

        return packId;
    }

    private static bool IsPathInsideDirectory(string directoryPath, string candidatePath)
    {
        var normalizedDirectory = directoryPath.EndsWith(Path.DirectorySeparatorChar)
            ? directoryPath
            : directoryPath + Path.DirectorySeparatorChar;

        return candidatePath.StartsWith(normalizedDirectory, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCompatibleWithAppVersion(string minAppVersion, Version appVersion)
    {
        return !Version.TryParse(minAppVersion, out var requiredVersion) || appVersion >= requiredVersion;
    }

    private static bool MatchesCatalogEntry(RomanizationPackCatalogEntry entry, RomanizationPackManifest manifest)
    {
        return string.Equals(entry.Id, manifest.Id, StringComparison.OrdinalIgnoreCase)
               && string.Equals(entry.EngineId, manifest.EngineId, StringComparison.OrdinalIgnoreCase)
               && string.Equals(entry.Version, manifest.Version, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesSha256(byte[] bytes, string expectedHex)
    {
        if (string.IsNullOrWhiteSpace(expectedHex)) return false;
        var actualBytes = SHA256.HashData(bytes);
        var actualHex = Convert.ToHexString(actualBytes);
        return string.Equals(actualHex, expectedHex.Replace("-", string.Empty), StringComparison.OrdinalIgnoreCase);
    }

    private static void ExtractZipSafely(string archivePath, string destinationPath)
    {
        Directory.CreateDirectory(destinationPath);
        var destinationRoot = Path.GetFullPath(destinationPath);
        if (!destinationRoot.EndsWith(Path.DirectorySeparatorChar))
            destinationRoot += Path.DirectorySeparatorChar;

        using var archive = ZipFile.OpenRead(archivePath);
        foreach (var entry in archive.Entries)
        {
            EnsureDataOnlyEntry(entry);

            var targetPath = Path.GetFullPath(Path.Combine(destinationRoot, entry.FullName));
            if (!targetPath.StartsWith(destinationRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Pack contains a path outside the extraction directory.");

            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(targetPath);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            entry.ExtractToFile(targetPath, true);
        }
    }

    private static void EnsureDataOnlyEntry(ZipArchiveEntry entry)
    {
        var normalizedName = entry.FullName.Replace('\\', '/');
        if (normalizedName.Contains(':', StringComparison.Ordinal))
            throw new InvalidOperationException("Pack contains an invalid file path.");

        if (string.IsNullOrEmpty(entry.Name)) return;

        var fileName = Path.GetFileName(normalizedName);
        if (fileName.Equals("LICENSE", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("NOTICE", StringComparison.OrdinalIgnoreCase))
            return;

        if (!AllowedPackExtensions.Contains(Path.GetExtension(fileName)))
            throw new InvalidOperationException("Pack contains a non-data file.");
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
        catch
        {
        }
    }
}
