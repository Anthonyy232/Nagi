using System;
using System.Diagnostics;
using System.Reflection;
using Windows.ApplicationModel;
using Nagi.Core.Services.Abstractions;

namespace Nagi.WinUI.Services.Implementations;

public class AppInfoService : IAppInfoService {
    public string GetAppName() {
        try {
            return Package.Current.DisplayName;
        }
        catch (InvalidOperationException) {
            // This can happen if the app is running unpackaged.
            return "Nagi.WinUI";
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