using System;
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
        try
        {
            return Package.Current.DisplayName;
        }
        catch (InvalidOperationException)
        {
            _logger.LogDebug("Application is running unpackaged. Falling back to default app name.");
            return "Nagi.WinUI";
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
            // For unpackaged apps, use assembly version
            var assembly = Assembly.GetEntryAssembly();
            if (assembly?.GetName().Version is { } version) return $"{version.Major}.{version.Minor}.{version.Build}";
#endif
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not get application version.");
        }

        return "N/A";
    }
}