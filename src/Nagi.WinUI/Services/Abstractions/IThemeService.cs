using System.Threading.Tasks;
using Microsoft.UI.Xaml;

namespace Nagi.WinUI.Services.Abstractions;

/// <summary>
///     Manages the application's visual theme.
/// </summary>
public interface IThemeService
{
    void ApplyTheme(ElementTheme theme);
    Task ReapplyCurrentDynamicThemeAsync();

    /// <summary>
    ///     Gets the currently applied application theme.
    /// </summary>
    ElementTheme CurrentTheme { get; }

    /// <summary>
    ///     Applies a dynamic theme color based on color swatches, respecting the current app theme (Light/Dark).
    /// </summary>
    /// <param name="lightSwatchId">The hex color string for the light theme.</param>
    /// <param name="darkSwatchId">The hex color string for the dark theme.</param>
    Task ApplyDynamicThemeFromSwatchesAsync(string? lightSwatchId, string? darkSwatchId);

    /// <summary>
    ///     Applies the configured accent color, or the default app accent color when none is configured.
    /// </summary>
    /// <param name="theme">The optional theme context to avoid redundant UI thread dispatches.</param>
    Task ActivateDefaultPrimaryColorAsync(ElementTheme? theme = null);

    /// <summary>
    ///     Applies the specified accent color to the application's primary color.
    /// </summary>
    /// <param name="color">The accent color to apply, or null to use the app default.</param>
    Task ApplyAccentColorAsync(Windows.UI.Color? color);
}
