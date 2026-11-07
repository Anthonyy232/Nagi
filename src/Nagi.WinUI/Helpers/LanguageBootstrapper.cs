using System.Globalization;
using Windows.Globalization;
using Nagi.WinUI.Services.Implementations;
using Nagi.Presentation.Services;

namespace Nagi.WinUI.Helpers;

public static class LanguageBootstrapper
{
    public static void Bootstrap()
    {
        try
        {
            var language = AppSettings.Language.ReadFrom(new ApplicationDataSettingsStore());

            if (!string.IsNullOrEmpty(language))
            {
                ApplicationLanguages.PrimaryLanguageOverride = language;

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
