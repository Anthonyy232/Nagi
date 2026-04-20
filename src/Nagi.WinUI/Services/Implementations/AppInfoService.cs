using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Windows.ApplicationModel;
using Microsoft.Extensions.Logging;
using Nagi.Core.Services.Abstractions;

namespace Nagi.WinUI.Services.Implementations;

public class AppInfoService : IAppInfoService
{
    private readonly ILogger<AppInfoService> _logger;

    public AppInfoService(ILogger<AppInfoService> logger)
    {
        _logger = logger;
    }

    public string GetAppName()
    {
        return Package.Current.DisplayName;
    }

    public string GetAppVersion()
    {
        var version = Package.Current.Id.Version;
        return $"{version.Major}.{version.Minor}.{version.Build}";
    }


    private IReadOnlyList<string>? _cachedAvailableLanguages;
    private readonly System.Threading.SemaphoreSlim _languageCacheLock = new(1, 1);

    public async System.Threading.Tasks.Task<IReadOnlyList<string>> GetAvailableLanguagesAsync()
    {
        if (_cachedAvailableLanguages != null)
            return _cachedAvailableLanguages;

        await _languageCacheLock.WaitAsync();
        try
        {
            if (_cachedAvailableLanguages != null)
                return _cachedAvailableLanguages;

            // Discover supported languages by scanning package subdirectories for satellite assemblies.
            // ManifestLanguages only reflects WinRT (.resw) resources; this app uses .resx satellite
            // assemblies. StorageFolder is used instead of Directory.EnumerateFiles because MSIX's VFS
            // blocks Win32 filesystem enumeration inside WindowsApps install directories.
            var satelliteName = Assembly.GetEntryAssembly()?.GetName().Name + ".resources.dll";
            var installFolder = Package.Current.InstalledLocation;
            _logger.LogDebug("Language scan: installFolder={Path}, satellite={Satellite}", installFolder.Path, satelliteName);

            var results = new List<string>();
            var subFolders = await installFolder.GetFoldersAsync();
            _logger.LogDebug("Language scan: {Count} subfolders found", subFolders.Count);

            foreach (var folder in subFolders)
            {
                try { _ = new CultureInfo(folder.Name); }
                catch (CultureNotFoundException) { continue; }

                var item = await folder.TryGetItemAsync(satelliteName);
                _logger.LogDebug("Language scan: {Folder}/{Satellite} -> {Found}", folder.Name, satelliteName, item != null ? "found" : "missing");
                if (item == null) continue;

                results.Add(folder.Name);
            }

            _logger.LogDebug("Language scan: completed with {Count} languages", results.Count);
            _cachedAvailableLanguages = results;
            return _cachedAvailableLanguages;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to enumerate available languages from package.");
            return Array.Empty<string>();
        }
        finally
        {
            _languageCacheLock.Release();
        }
    }
    public async System.Threading.Tasks.Task InitializeAsync()
    {
        // Pre-cache languages
        await GetAvailableLanguagesAsync();
    }
}
