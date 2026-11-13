using CommunityToolkit.Mvvm.ComponentModel;
using Nagi.Core.OnlineProviders;
using Nagi.Presentation.Common;
using Nagi.Presentation.Resources;

namespace Nagi.Presentation.Settings;

public partial class ServiceProviderSettingViewModel : ObservableObject, IToggleItem
{
    private readonly OnlineProvider _provider;

    public ServiceProviderSettingViewModel(ServiceProviderSetting setting)
    {
        _provider = ProviderCatalog.Get(setting.Id);
        IsEnabled = setting.IsEnabled;
    }

    [ObservableProperty] public partial bool IsEnabled { get; set; }

    public string Id => _provider.Id;
    public string DisplayName => _provider.DisplayName;
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

    /// <summary>Other metadata providers look artists and albums up by the IDs this one finds, so it stays first.</summary>
    public bool IsPinnedFirst => _provider.Can(ProviderCapabilities.IdResolver);

    public ServiceProviderSetting ToSetting() => new(Id, IsEnabled);
}
