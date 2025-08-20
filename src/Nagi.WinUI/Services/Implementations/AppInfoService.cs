using System;
using System.Diagnostics;
using Windows.ApplicationModel;
using Nagi.Core.Services.Abstractions;

namespace Nagi.WinUI.Services.Implementations;

public class AppInfoService : IAppInfoService
{
    public string GetAppName()
    {
        try
        {
            return Package.Current.DisplayName;
        }
        catch (InvalidOperationException)
        {
            // This can happen if the app is running unpackaged.
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
                        if (assembly?.GetName().Version is { } version) {
                            return $"{version.Major}.{version.Minor}.{version.Build}";
                        }
#endif
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ERROR] Could not get application version: {ex.Message}");
        }

        return "N/A";
    }
}