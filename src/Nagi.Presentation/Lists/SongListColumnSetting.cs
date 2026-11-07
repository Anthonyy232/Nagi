using CommunityToolkit.Mvvm.ComponentModel;

namespace Nagi.Presentation.Lists;

public partial class SongListColumnSetting(SongListColumns column) : ObservableObject
{
    [ObservableProperty] public partial bool IsEnabled { get; set; }

    public SongListColumns Column { get; } = column;

    public string DisplayName { get; } = SongListColumnsHelper.GetLabel(column);
}
