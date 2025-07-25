using Microsoft.UI.Xaml;

namespace Nagi.WinUI.Services.Abstractions;

/// <summary>
/// Manages the application's visual theme.
/// </summary>
public interface IThemeService {
    void ApplyTheme(ElementTheme theme);
    void ReapplyCurrentDynamicTheme();

    /// <summary>
    /// Applies a dynamic theme color based on color swatches, respecting the current app theme (Light/Dark).
    /// </summary>
    /// <param name="lightSwatchId">The hex color string for the light theme.</param>
    /// <param name="darkSwatchId">The hex color string for the dark theme.</param>
    void ApplyDynamicThemeFromSwatches(string? lightSwatchId, string? darkSwatchId);

    /// <summary>
    /// Resets the application's primary color to the default system accent color.
    /// </summary>
    void ActivateDefaultPrimaryColor();
}