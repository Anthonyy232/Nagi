using CommunityToolkit.Mvvm.ComponentModel;

namespace Nagi.Presentation.Navigation;

public partial class NavigationItemSetting : ObservableObject
{
    [ObservableProperty] public partial bool IsEnabled { get; set; }

    public string Tag { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string IconGlyph { get; set; } = string.Empty;
}
