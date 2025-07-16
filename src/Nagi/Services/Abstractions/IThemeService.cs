using Microsoft.UI.Xaml;

namespace Nagi.Services.Abstractions;

/// <summary>
/// Manages the application's visual theme.
/// </summary>
public interface IThemeService {
    void ApplyTheme(ElementTheme theme);
    void ReapplyCurrentDynamicTheme();
}