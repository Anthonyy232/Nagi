using CommunityToolkit.Mvvm.ComponentModel;
using Nagi.Presentation.Common;
using Nagi.Presentation.Settings;

namespace Nagi.Presentation.Navigation;

public partial class NavigationItemSetting : ObservableObject, IToggleItem
{
    [ObservableProperty] public partial bool IsEnabled { get; set; }

    public string Tag { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public AppIcon Icon { get; set; }

    public override string ToString() => DisplayName;
}
