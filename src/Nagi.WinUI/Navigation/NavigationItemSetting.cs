using CommunityToolkit.Mvvm.ComponentModel;

namespace Nagi.WinUI.Navigation;

/// <summary>
///     Represents the settings for a single item in the main navigation view.
/// </summary>
public partial class NavigationItemSetting : ObservableObject
{
    [ObservableProperty] public partial bool IsEnabled { get; set; }

    /// <summary>
    ///     Gets or sets a unique identifier for the navigation item.
    /// </summary>
    public string Tag { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the text displayed for the navigation item in the UI.
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the glyph character for the item's icon.
    /// </summary>
    public string IconGlyph { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the font family for the item's icon, if it's not the default symbol font.
    /// </summary>
    public string? IconFontFamily { get; set; }
}