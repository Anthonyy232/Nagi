using CommunityToolkit.Mvvm.ComponentModel;

namespace Nagi.WinUI.Navigation;

public partial class NavigationItemSetting : ObservableObject
{
    [ObservableProperty] public partial bool IsEnabled { get; set; }

    public string Tag { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string IconGlyph { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the font family for the item's icon, if it's not the default symbol font.
    /// </summary>
    public string? IconFontFamily { get; set; }
}
