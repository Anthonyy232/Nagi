using System.IO.Compression;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Nagi.Core.Platform;

namespace Nagi.Core.Lyrics.Romanization;

public sealed class RomanizationPackManager : IRomanizationPackManager
{
    /// <summary>The release that holds the signed catalog and every pack it lists.</summary>
    public const string ReleaseFolderUrl = AppLinks.Repository + "/releases/download/romanization-packs";

    internal const string CatalogUrl = ReleaseFolderUrl + "/catalog.json";

    // Directories being extracted or replaced end with this, so listing the installed packs skips them.
    private const string StagingExtension = ".tmp";

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
    private readonly IFileSystemService _fileSystem;
    private readonly IAppInfoService _appInfo;
    private RomanizationPackCatalog? _cachedCatalog;

    public event Action? PacksChanged;

    public RomanizationPackManager(
        IHttpClientFactory httpClientFactory,
        IPathConfiguration pathConfig,
        IEnumerable<IRomanizationProvider> providers,
        IRomanizationCatalogVerifier catalogVerifier,
        IFileSystemService fileSystem,
        IAppInfoService appInfo,
        ILogger<RomanizationPackManager> logger)
    {
        _httpClient = httpClientFactory.CreateClient();
        _pathConfig = pathConfig;
        _providers = providers.ToDictionary(p => p.EngineId, StringComparer.OrdinalIgnoreCase);
        _catalogVerifier = catalogVerifier;
        _fileSystem = fileSystem;
        _appInfo = appInfo;
        _logger = logger;
    }

    public async Task<IReadOnlyList<RomanizationPackView>> GetAvailablePacksAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        var catalog = await GetCatalogAsync(forceRefresh, cancellationToken).ConfigureAwait(false);
        var installed = await GetInstalledPacksAsync(cancellationToken).ConfigureAwait(false);
        var installedById = installed.ToDictionary(p => p.Manifest.Id, StringComparer.OrdinalIgnoreCase);
        var appVersion = ToFourPartVersion(_appInfo.GetAppVersion()) ?? new Version(0, 0, 0, 0);

        return catalog.Packs
            .Where(p => _providers.ContainsKey(p.EngineId))
            .Where(p => ToFourPartVersion(p.MinAppVersion) is not { } required || appVersion >= required)
            .Select(p => new RomanizationPackView
            {
                CatalogEntry = p,
                InstalledPack = installedById.TryGetValue(p.Id, out var installedPack) ? installedPack : null
            })
            .OrderBy(p => p.CatalogEntry.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public async Task<IReadOnlyList<InstalledRomanizationPack>> GetInstalledPacksAsync(CancellationToken cancellationToken = default)
    {
        _fileSystem.CreateDirectory(PackRoot);
        var packs = new List<InstalledRomanizationPack>();

        foreach (var directory in _fileSystem.EnumerateDirectories(PackRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directoryName = Path.GetFileName(directory);
            if (directoryName.EndsWith(StagingExtension, StringComparison.OrdinalIgnoreCase)) continue;

            var manifestPath = Path.Combine(directory, "manifest.json");
            if (!_fileSystem.FileExists(manifestPath)) continue;

            try
            {
                var manifest = await ReadManifestAsync(manifestPath).ConfigureAwait(false);

                if (manifest is null || string.IsNullOrWhiteSpace(manifest.Id)) continue;
                if (!string.Equals(directoryName, SanitizePackId(manifest.Id), StringComparison.OrdinalIgnoreCase)) continue;

                packs.Add(new InstalledRomanizationPack { Manifest = manifest, DirectoryPath = directory });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Skipping invalid romanization pack manifest at {Path}", manifestPath);
            }
        }

        return packs;
    }

    public async Task<RomanizationPackOperationResult> InstallPackAsync(string packId, CancellationToken cancellationToken = default)
    {
        RomanizationPackOperationResult Rejected(string reason)
        {
            _logger.LogWarning("Romanization pack {PackId} was not installed: {Reason}", packId, reason);
            return RomanizationPackOperationResult.Failure(reason);
        }

        try
        {
            var pack = (await GetAvailablePacksAsync(false, cancellationToken).ConfigureAwait(false))
                .FirstOrDefault(p => string.Equals(p.CatalogEntry.Id, packId, StringComparison.OrdinalIgnoreCase))
                ?.CatalogEntry;

            if (pack is null) return Rejected("Pack is not available for this app version.");
            var provider = _providers[pack.EngineId];
            if (!Uri.TryCreate(pack.DownloadUrl, UriKind.Absolute, out var downloadUri) || downloadUri.Scheme != Uri.UriSchemeHttps)
                return Rejected("Pack download URL must use HTTPS.");

            var tempArchivePath = Path.Combine(PackRoot, $"{Guid.NewGuid():N}.nagipack");
            var tempExtractPath = StagingPath(Path.Combine(PackRoot, "install"));

            try
            {
                // GetStreamAsync's HTTP timeout ends at the headers; cover the body transfer too.
                using var downloadCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                downloadCts.CancelAfter(_httpClient.Timeout);
                var downloadToken = downloadCts.Token;
                await using (var download = await _httpClient.GetStreamAsync(downloadUri, downloadToken).ConfigureAwait(false))
                await using (var archive = _fileSystem.CreateFile(tempArchivePath))
                {
                    await download.CopyToAsync(archive, downloadToken).ConfigureAwait(false);
                    archive.Position = 0;
                    var hash = await SHA256.HashDataAsync(archive, downloadToken).ConfigureAwait(false);
                    if (!string.Equals(Convert.ToHexString(hash), pack.Sha256?.Replace("-", string.Empty), StringComparison.OrdinalIgnoreCase))
                        return Rejected("Downloaded pack hash did not match the signed catalog.");
                }
                ExtractZipSafely(tempArchivePath, tempExtractPath);

                var manifestPath = Path.Combine(tempExtractPath, "manifest.json");
                if (!_fileSystem.FileExists(manifestPath)) return Rejected("Pack does not contain a manifest.");

                var manifest = await ReadManifestAsync(manifestPath).ConfigureAwait(false);
                if (manifest is null) return Rejected("Pack manifest could not be read.");
                if (!MatchesCatalogEntry(pack, manifest)) return Rejected("Pack manifest does not match the signed catalog.");

                var installedPack = new InstalledRomanizationPack { Manifest = manifest, DirectoryPath = tempExtractPath };
                if (!await RomanizationPackFiles.ValidateAsync(_fileSystem, provider, installedPack, cancellationToken).ConfigureAwait(false))
                    return Rejected("Pack self-test failed.");

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
            if (_fileSystem.DirectoryExists(path))
            {
                _fileSystem.DeleteDirectory(path, true);
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
                           CatalogUrl, RomanizationJson.Options, cancellationToken).ConfigureAwait(false)
                       ?? throw new InvalidOperationException("Catalog response was empty.");

        if (!_catalogVerifier.Verify(envelope))
            throw new InvalidOperationException("Romanization pack catalog signature is invalid.");

        _cachedCatalog = envelope.Catalog;
        return _cachedCatalog;
    }

    private string PackRoot => _pathConfig.RomanizationPacksPath;

    private static string StagingPath(string path) => $"{path}.{Guid.NewGuid():N}{StagingExtension}";

    private async Task<RomanizationPackManifest?> ReadManifestAsync(string path) =>
        JsonSerializer.Deserialize<RomanizationPackManifest>(
            await _fileSystem.ReadAllTextAsync(path).ConfigureAwait(false), RomanizationJson.Options);

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

    // The app reports three parts and catalogs may give four, which Version would rank below them when equal.
    private static Version? ToFourPartVersion(string? version) =>
        Version.TryParse(version, out var parsed)
            ? new Version(parsed.Major, parsed.Minor, Math.Max(parsed.Build, 0), Math.Max(parsed.Revision, 0))
            : null;

    private static bool MatchesCatalogEntry(RomanizationPackCatalogEntry entry, RomanizationPackManifest manifest)
    {
        return string.Equals(entry.Id, manifest.Id, StringComparison.OrdinalIgnoreCase)
               && string.Equals(entry.EngineId, manifest.EngineId, StringComparison.OrdinalIgnoreCase)
               && string.Equals(entry.Version, manifest.Version, StringComparison.OrdinalIgnoreCase);
    }

    private void ReplacePackDirectory(string stagedPath, string installedPath)
    {
        if (!_fileSystem.DirectoryExists(installedPath))
        {
            _fileSystem.MoveDirectory(stagedPath, installedPath);
            return;
        }

        var backupPath = StagingPath(installedPath);
        _fileSystem.MoveDirectory(installedPath, backupPath);
        try
        {
            _fileSystem.MoveDirectory(stagedPath, installedPath);
        }
        catch
        {
            _fileSystem.MoveDirectory(backupPath, installedPath);
            throw;
        }

        TryDeleteDirectory(backupPath);
    }

    private void ExtractZipSafely(string archivePath, string destinationPath)
    {
        using var stream = _fileSystem.OpenRead(archivePath);
        using var archive = new ZipArchive(stream);
        foreach (var entry in archive.Entries)
            EnsureDataOnlyEntry(entry);

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destinationPath)) + Path.DirectorySeparatorChar;
        _fileSystem.CreateDirectory(root);
        foreach (var entry in archive.Entries)
        {
            var path = Path.GetFullPath(Path.Combine(root, entry.FullName));
            if (!path.StartsWith(root, StringComparison.Ordinal))
                throw new InvalidOperationException("Pack contains a file outside its folder.");

            if (string.IsNullOrEmpty(entry.Name))
            {
                _fileSystem.CreateDirectory(path);
                continue;
            }

            _fileSystem.CreateDirectory(Path.GetDirectoryName(path)!);
            using var source = entry.Open();
            using var target = _fileSystem.CreateFile(path);
            source.CopyTo(target);
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

    private void TryDeleteFile(string path)
    {
        try
        {
            _fileSystem.DeleteFile(path);
        }
        catch
        {
        }
    }

    private void TryDeleteDirectory(string path)
    {
        try
        {
            if (_fileSystem.DirectoryExists(path)) _fileSystem.DeleteDirectory(path, true);
        }
        catch
        {
        }
    }
}
