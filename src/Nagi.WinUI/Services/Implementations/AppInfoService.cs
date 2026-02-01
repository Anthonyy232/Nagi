using System;
using System.Reflection;
using Windows.ApplicationModel;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
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
        try
        {
            return Package.Current.DisplayName;
        }
        catch (InvalidOperationException)
        {
            _logger.LogDebug("Application is running unpackaged. Falling back to default app name.");
            return Resources.Strings.App_Name_Default;
        }
    }

    public string GetAppVersion()
    {
        try
        {
#if MSIX_PACKAGE
            // For MSIX packages, get the version from the package manifest
            var package = Package.Current;
            var version = package.Id.Version;
            return $"{version.Major}.{version.Minor}.{version.Build}";
#else
            // For unpackaged apps, use assembly version from this type's assembly
            var assembly = typeof(AppInfoService).Assembly;
            if (assembly.GetName().Version is { } version) return $"{version.Major}.{version.Minor}.{version.Build}";
#endif
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not get application version.");
        }

        return Resources.Strings.App_Version_Unknown;
    }


    private IReadOnlyList<string>? _cachedAvailableLanguages;
    private readonly System.Threading.SemaphoreSlim _languageCacheLock = new(1, 1);

    public async System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<string>> GetAvailableLanguagesAsync()
    {
        if (_cachedAvailableLanguages != null)
        {
            return _cachedAvailableLanguages;
        }

        await _languageCacheLock.WaitAsync();
        try
        {
            if (_cachedAvailableLanguages != null)
            {
                return _cachedAvailableLanguages;
            }

            try
            {
                // Try to get from package identity
                _cachedAvailableLanguages = Windows.Globalization.ApplicationLanguages.ManifestLanguages;
                return _cachedAvailableLanguages;
            }
            catch (InvalidOperationException)
            {
                _logger.LogDebug("Application is running unpackaged. Scanning directory for languages.");
            }

            // Offload IO to background thread
            _cachedAvailableLanguages = await System.Threading.Tasks.Task.Run(() => 
            {
                var languages = new System.Collections.Generic.List<string>();
                try 
                {
                    var baseDir = AppContext.BaseDirectory;
                    if (System.IO.Directory.Exists(baseDir))
                    {
                        var resourceDllName = typeof(AppInfoService).Assembly.GetName().Name + ".resources.dll";
                        
                        foreach (var dir in System.IO.Directory.GetDirectories(baseDir))
                        {
                             var dirName = System.IO.Path.GetFileName(dir);
                             try
                             {
                                 // Validate if the directory name is a valid culture
                                 var culture = System.Globalization.CultureInfo.GetCultureInfo(dirName);
                                 
                                 // Check if it contains the main resource assembly
                                 // This filters out directories that happen to be named like a culture (e.g. "bin" is not a culture, but "id" is)
                                 // but don't contain resources.
                                 if (System.IO.File.Exists(System.IO.Path.Combine(dir, resourceDllName)))
                                 {
                                     languages.Add(dirName);
                                 }
                             }
                             catch (System.Globalization.CultureNotFoundException)
                             {
                                 // Not a valid culture directory, ignore
                             }
                        }
                    }
                }
                catch (Exception ex)
                {
                     _logger.LogWarning(ex, "Error scanning languages directory.");
                }
                return languages;
            });

            return _cachedAvailableLanguages;
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