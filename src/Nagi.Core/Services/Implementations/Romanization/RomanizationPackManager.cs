using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Json;
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
        Directory.CreateDirectory(PackRoot);
        var packs = new List<InstalledRomanizationPack>();

        foreach (var directory in Directory.EnumerateDirectories(PackRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directoryName = Path.GetFileName(directory);
            if (directoryName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;

            var manifestPath = Path.Combine(directory, "manifest.json");
            if (!File.Exists(manifestPath)) continue;

            try
            {
                using var stream = File.OpenRead(manifestPath);
                var manifest = JsonSerializer.Deserialize<RomanizationPackManifest>(stream, RomanizationJson.Options);

                if (manifest is null || string.IsNullOrWhiteSpace(manifest.Id)) continue;
                if (!string.Equals(directoryName, SanitizePackId(manifest.Id), StringComparison.OrdinalIgnoreCase)) continue;

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
            var provider = _providers[pack.EngineId];
            if (!Uri.TryCreate(pack.DownloadUrl, UriKind.Absolute, out var downloadUri) || downloadUri.Scheme != Uri.UriSchemeHttps)
                return RomanizationPackOperationResult.Failure("Pack download URL must use HTTPS.");

            var tempArchivePath = Path.Combine(PackRoot, $"{Guid.NewGuid():N}.nagipack");
            var tempExtractPath = Path.Combine(PackRoot, $"{Guid.NewGuid():N}.tmp");

            try
            {
                // GetStreamAsync's HTTP timeout ends at the headers; cover the body transfer too.
                using var downloadCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                downloadCts.CancelAfter(_httpClient.Timeout);
                var downloadToken = downloadCts.Token;
                await using (var download = await _httpClient.GetStreamAsync(downloadUri, downloadToken).ConfigureAwait(false))
                await using (var archive = File.Create(tempArchivePath))
                {
                    await download.CopyToAsync(archive, downloadToken).ConfigureAwait(false);
                    archive.Position = 0;
                    var hash = await SHA256.HashDataAsync(archive, downloadToken).ConfigureAwait(false);
                    if (!string.Equals(Convert.ToHexString(hash), pack.Sha256?.Replace("-", string.Empty), StringComparison.OrdinalIgnoreCase))
                        return RomanizationPackOperationResult.Failure("Downloaded pack hash did not match the signed catalog.");
                }
                ExtractZipSafely(tempArchivePath, tempExtractPath);

                var manifestPath = Path.Combine(tempExtractPath, "manifest.json");
                if (!File.Exists(manifestPath)) return RomanizationPackOperationResult.Failure("Pack does not contain a manifest.");

                RomanizationPackManifest? manifest;
                await using (var stream = File.OpenRead(manifestPath))
                    manifest = await JsonSerializer.DeserializeAsync<RomanizationPackManifest>(
                        stream, RomanizationJson.Options, cancellationToken).ConfigureAwait(false);

                if (manifest is null) return RomanizationPackOperationResult.Failure("Pack manifest could not be read.");
                if (!MatchesCatalogEntry(pack, manifest)) return RomanizationPackOperationResult.Failure("Pack manifest does not match the signed catalog.");

                var installedPack = new InstalledRomanizationPack { Manifest = manifest, DirectoryPath = tempExtractPath };
                if (!await provider.ValidatePackAsync(installedPack, cancellationToken).ConfigureAwait(false))
                    return RomanizationPackOperationResult.Failure("Pack self-test failed.");

                cancellationToken.ThrowIfCancellationRequested();
                ReplacePackDirectory(tempExtractPath, GetPackDirectory(pack.Id));
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

        var envelope = await _httpClient.GetFromJsonAsync<RomanizationCatalogEnvelope>(
                           DefaultCatalogUrl, RomanizationJson.Options, cancellationToken).ConfigureAwait(false)
                       ?? throw new InvalidOperationException("Catalog response was empty.");

        if (!_catalogVerifier.Verify(envelope))
            throw new InvalidOperationException("Romanization pack catalog signature is invalid.");

        _cachedCatalog = envelope.Catalog;
        return _cachedCatalog;
    }

    private string PackRoot => _pathConfig.RomanizationPacksPath;

    private string GetPackDirectory(string packId)
    {
        return Path.GetFullPath(Path.Combine(PackRoot, SanitizePackId(packId)));
    }

    private static string SanitizePackId(string packId)
    {
        if (string.IsNullOrWhiteSpace(packId)) throw new ArgumentException("Pack ID is required.", nameof(packId));

        packId = packId.Trim();
        if (packId.StartsWith(".", StringComparison.Ordinal)
            || packId.EndsWith(".", StringComparison.Ordinal)
            || packId.Contains("..", StringComparison.Ordinal))
            throw new ArgumentException("Pack ID is invalid.", nameof(packId));

        if (packId.Any(c => !char.IsLetterOrDigit(c) && c is not '-' and not '_' and not '.'))
            throw new ArgumentException("Pack ID contains invalid characters.", nameof(packId));

        return packId;
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

    private static void ReplacePackDirectory(string stagedPath, string installedPath)
    {
        if (!Directory.Exists(installedPath))
        {
            Directory.Move(stagedPath, installedPath);
            return;
        }

        var backupPath = installedPath + $".{Guid.NewGuid():N}.tmp";
        Directory.Move(installedPath, backupPath);
        try
        {
            Directory.Move(stagedPath, installedPath);
        }
        catch
        {
            Directory.Move(backupPath, installedPath);
            throw;
        }

        TryDeleteDirectory(backupPath);
    }

    private static void ExtractZipSafely(string archivePath, string destinationPath)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        foreach (var entry in archive.Entries)
            EnsureDataOnlyEntry(entry);
        archive.ExtractToDirectory(destinationPath, overwriteFiles: true);
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
            File.Delete(path);
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
