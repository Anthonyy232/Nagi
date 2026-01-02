using System;
using Microsoft.Extensions.Logging;
using Microsoft.Windows.ApplicationModel.Resources;
using Nagi.WinUI.Services.Abstractions;

namespace Nagi.WinUI.Services.Implementations;

/// <summary>
///     Implementation of ILocalizationService using the Windows App SDK ResourceLoader.
///     Thread-safe and designed for DI singleton usage.
/// </summary>
public class LocalizationService : ILocalizationService
{
    private readonly ResourceLoader _resourceLoader;
    private readonly ILogger<LocalizationService> _logger;

    public LocalizationService(ILogger<LocalizationService> logger)
    {
        _logger = logger;
        _resourceLoader = new ResourceLoader();
    }

    public string GetString(string key)
    {
        return GetString(key, key);
    }

    public string GetString(string key, string fallback)
    {
        if (string.IsNullOrEmpty(key)) return fallback;

        try
        {
            var value = _resourceLoader.GetString(key);
            if (string.IsNullOrEmpty(value))
            {
                _logger.LogDebug("Resource key '{Key}' not found, using fallback", key);
                return fallback;
            }
            return value;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load resource for key '{Key}'", key);
            return fallback;
        }
    }

    public string GetFormattedString(string key, params object[] args)
    {
        var template = GetString(key);
        if (args.Length == 0) return template;

        try
        {
            return string.Format(template, args);
        }
        catch (FormatException ex)
        {
            _logger.LogWarning(ex, "Format error for key '{Key}' with {ArgCount} arguments", key, args.Length);
            return template;
        }
    }
}
