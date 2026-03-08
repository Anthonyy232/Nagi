using System;
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
        return Package.Current.DisplayName;
    }

    public string GetAppVersion()
    {
        try
        {
            var package = Package.Current;
            var version = package.Id.Version;
            return $"{version.Major}.{version.Minor}.{version.Build}";
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

            _cachedAvailableLanguages = Windows.Globalization.ApplicationLanguages.ManifestLanguages;
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