using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using Windows.ApplicationModel;
using Windows.Globalization;
using Windows.Storage;

namespace Nagi.WinUI.Helpers;

/// <summary>
///     Helper class to bootstrap the application language settings before the main UI initializes.
/// </summary>
public static class LanguageBootstrapper
{
    public static void Bootstrap()
    {
        try
        {
            string? language = null;
            var isPackaged = false;

            try
            {
                // Check if we are packaged
                isPackaged = Package.Current != null;
            }
            catch
            {
                isPackaged = false;
            }

            if (isPackaged)
            {
                if (ApplicationData.Current.LocalSettings.Values.TryGetValue("AppLanguage", out var val) && val is string s)
                {
                    language = s;
                }
            }
            else
            {
                // Calculate settings path manually to facilitate lightweight bootstrap without IO/Directory creation
                string appDataRoot;
                try
                {
                    appDataRoot = ApplicationData.Current.LocalFolder.Path;
                }
                catch
                {
                    appDataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nagi");
                }

                var settingsPath = Path.Combine(appDataRoot, "settings.json");

                if (File.Exists(settingsPath))
                {
                    // Use synchronous read to block Main thread execution until language is set
                    // We only need this one value, so lightweight parsing is better
                    var json = File.ReadAllText(settingsPath);
                    using var doc = JsonDocument.Parse(json);
                    
                    if (doc.RootElement.TryGetProperty("AppLanguage", out var element) && 
                        element.ValueKind == JsonValueKind.String)
                    {
                        language = element.GetString();
                    }
                }
            }

            if (!string.IsNullOrEmpty(language))
            {
                // Set the WinRT language override
                ApplicationLanguages.PrimaryLanguageOverride = language;
                
                // Set .NET culture
                var culture = new CultureInfo(language);
                CultureInfo.CurrentUICulture = culture;
                CultureInfo.CurrentCulture = culture;
                CultureInfo.DefaultThreadCurrentUICulture = culture;
                CultureInfo.DefaultThreadCurrentCulture = culture;
            }
            else
            {
                // If the setting is empty (System Default), we must clear the override in case it was set previously.
                // This allows the app to revert to matching the system language.
                ApplicationLanguages.PrimaryLanguageOverride = string.Empty;
            }
        }
        catch (Exception)
        {
            // Silently fail during bootstrap - defaults will be used.
            // We avoid logging here as the logging infrastructure isn't set up yet.
        }
    }
}
