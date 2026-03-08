using System;
using System.Globalization;
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

            if (ApplicationData.Current.LocalSettings.Values.TryGetValue("AppLanguage", out var val) && val is string s)
            {
                language = s;
            }

            if (!string.IsNullOrEmpty(language))
            {
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
                // If the setting is empty (System Default), clear the override so the app reverts to the system language.
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
