using CommunityToolkit.Mvvm.ComponentModel;
using Nagi.Core.Models;
using Nagi.Presentation.Models;
using Nagi.Presentation.Resources;

namespace Nagi.Presentation.ViewModels;

public partial class ServiceProviderSettingViewModel : ObservableObject, IToggleItem
{
    [ObservableProperty] public partial bool IsEnabled { get; set; }

    public string Id { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public AppIcon Icon => AppIcon.OnlineSource;
    public string? Description => Id switch
    {
        ServiceProviderIds.LrcLib => Strings.Settings_Provider_LRCLIB_Desc,
        ServiceProviderIds.NetEase => Strings.Settings_Provider_NetEase_Desc,
        ServiceProviderIds.MusicBrainz => Strings.Settings_Provider_MusicBrainz_Desc,
        ServiceProviderIds.Wikipedia => Strings.Settings_Provider_Wikipedia_Desc,
        ServiceProviderIds.CoverArtArchive => Strings.Settings_Provider_CoverArtArchive_Desc,
        ServiceProviderIds.FanartTv => Strings.Settings_Provider_FanartTv_Desc,
        ServiceProviderIds.TheAudioDb => Strings.Settings_Provider_TheAudioDB_Desc,
        ServiceProviderIds.Deezer => Strings.Settings_Provider_Deezer_Desc,
        ServiceProviderIds.AppleMusic => Strings.Settings_Provider_AppleMusic_Desc,
        ServiceProviderIds.LastFm => Strings.Settings_Provider_LastFm_Desc,
        _ => null
    };

    public ServiceCategory Category { get; init; }

    /// <summary>MusicBrainz looks up the IDs other metadata providers use, so it stays first.</summary>
    public bool IsPinnedFirst => Id == ServiceProviderIds.MusicBrainz;

    public static ServiceProviderSettingViewModel FromSetting(ServiceProviderSetting setting)
    {
        return new ServiceProviderSettingViewModel
        {
            Id = setting.Id,
            DisplayName = setting.DisplayName,
            Category = setting.Category,
            IsEnabled = setting.IsEnabled
        };
    }

    public ServiceProviderSetting ToSetting(int order)
    {
        return new ServiceProviderSetting
        {
            Id = Id,
            DisplayName = DisplayName,
            Category = Category,
            IsEnabled = IsEnabled,
            Order = order
        };
    }
}
