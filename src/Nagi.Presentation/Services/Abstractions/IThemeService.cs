using System.Threading.Tasks;
using Nagi.Presentation.Models;

namespace Nagi.Presentation.Services.Abstractions;

public interface IThemeService
{
    void ApplyTheme(AppTheme theme);
    Task ReapplyCurrentDynamicThemeAsync();

    AppTheme CurrentTheme { get; }

    /// <summary>
    ///     Applies a dynamic theme color based on color swatches, respecting the current app theme (Light/Dark).
    /// </summary>
    /// <param name="lightSwatchId">The hex color string for the light theme.</param>
    /// <param name="darkSwatchId">The hex color string for the dark theme.</param>
    Task ApplyDynamicThemeFromSwatchesAsync(string? lightSwatchId, string? darkSwatchId);

    /// <summary>
    ///     Applies the configured accent color, or the default app accent color when none is configured.
    /// </summary>
    Task ActivateDefaultPrimaryColorAsync();

    /// <summary>
    ///     Applies the specified accent color to the application's primary color.
    /// </summary>
    /// <param name="color">The accent color to apply, or null to use the app default.</param>
    Task ApplyAccentColorAsync(ArgbColor? color);
}
