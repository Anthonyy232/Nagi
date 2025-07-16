using Nagi.Services.Abstractions;
using System;
using System.Diagnostics;
using System.Reflection;
using Windows.ApplicationModel;

namespace Nagi.Services.Implementations.WinUI;

public class WinUIAppInfoService : IAppInfoService {
    public string GetAppName() {
        try {
            return Package.Current.DisplayName;
        }
        catch (InvalidOperationException) {
            // This can happen if the app is running unpackaged.
            return "Nagi";
        }
    }

    public string GetAppVersion() {
        try {
            // This approach works for both packaged and unpackaged apps.
            var assembly = Assembly.GetEntryAssembly();
            if (assembly?.GetName().Version is { } version) {
                return $"{version.Major}.{version.Minor}.{version.Build}";
            }
        }
        catch (Exception ex) {
            Debug.WriteLine($"[ERROR] Could not get application version: {ex.Message}");
        }
        return "N/A";
    }
}