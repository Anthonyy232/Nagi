using Nagi.Presentation.Models;

namespace Nagi.Presentation.Services.Abstractions;

/// <summary>
///     Shows a theme and its colors in the UI head's windows. Call it on the UI thread.
/// </summary>
public interface IThemeService
{
    /// <summary>
    ///     Whether the main window shows the light theme. With <see cref="AppTheme.Default" /> this follows the system.
    /// </summary>
    bool IsLightTheme { get; }

    /// <summary>
    ///     Raised on the UI thread when the main window switches between light and dark.
    /// </summary>
    event Action? ActualThemeChanged;

    void SetTheme(AppTheme theme);

    void SetColors(ThemePalette palette);
}
