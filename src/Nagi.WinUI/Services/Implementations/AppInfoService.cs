using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Microsoft.Extensions.Logging;
using Nagi.Core.Services.Abstractions;
using Nagi.Presentation.Resources;

namespace Nagi.WinUI.Services.Implementations;

public class AppInfoService : IAppInfoService
{
    private readonly ILogger<AppInfoService> _logger;
    private readonly SemaphoreSlim _languageCacheLock = new(1, 1);
    private IReadOnlyList<string>? _cachedAvailableLanguages;

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

    public Task InitializeAsync() => GetAvailableLanguagesAsync();

    public async Task<IReadOnlyList<string>> GetAvailableLanguagesAsync()
    {
        if (_cachedAvailableLanguages != null)
            return _cachedAvailableLanguages;

        await _languageCacheLock.WaitAsync();
        try
        {
            if (_cachedAvailableLanguages != null)
                return _cachedAvailableLanguages;

            // ManifestLanguages only reflects WinRT (.resw) resources, but the UI strings live in .resx
            // satellite assemblies, so look for those in the package's locale folders instead.
            // StorageFolder is used because MSIX's VFS blocks Win32 enumeration of WindowsApps folders.
            var satelliteName = typeof(Strings).Assembly.GetName().Name + ".resources.dll";
            var results = new List<string>();

            foreach (var folder in await Package.Current.InstalledLocation.GetFoldersAsync())
            {
                try { _ = CultureInfo.GetCultureInfo(folder.Name); }
                catch (CultureNotFoundException) { continue; }

                if (await folder.TryGetItemAsync(satelliteName) != null)
                    results.Add(folder.Name);
            }

            _logger.LogDebug("Found {Count} UI languages in the package", results.Count);
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
}
