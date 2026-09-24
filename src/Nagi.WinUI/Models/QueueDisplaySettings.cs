using CommunityToolkit.Mvvm.ComponentModel;

namespace Nagi.WinUI.Models;

public partial class QueueDisplaySettings : ObservableObject
{
    [ObservableProperty] public partial bool ShowArtist { get; set; } = true;
    [ObservableProperty] public partial bool ShowDuration { get; set; } = true;
    [ObservableProperty] public partial bool ShowTrackCount { get; set; } = true;
    [ObservableProperty] public partial bool ShowPosition { get; set; }
    [ObservableProperty] public partial bool ShowGenre { get; set; }
    [ObservableProperty] public partial bool ShowTotalDuration { get; set; }
}
