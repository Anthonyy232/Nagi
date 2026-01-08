using CommunityToolkit.Mvvm.ComponentModel;
using Nagi.Core.Models;

namespace Nagi.WinUI.ViewModels;

/// <summary>
///     Observable wrapper for <see cref="ServiceProviderSetting" /> for UI binding.
/// </summary>
public partial class ServiceProviderSettingViewModel : ObservableObject
{
    [ObservableProperty] public partial bool IsEnabled { get; set; }

    public string Id { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string? Description { get; init; }
    public ServiceCategory Category { get; init; }
 
    public bool CanDrag => Id != ServiceProviderIds.MusicBrainz;

    /// <summary>
    ///     Creates a ViewModel from a settings model.
    /// </summary>
    public static ServiceProviderSettingViewModel FromSetting(ServiceProviderSetting setting)
    {
        return new ServiceProviderSettingViewModel
        {
            Id = setting.Id,
            DisplayName = setting.DisplayName,
            Description = setting.Description,
            Category = setting.Category,
            IsEnabled = setting.IsEnabled
        };
    }

    /// <summary>
    ///     Converts this ViewModel back to a settings model.
    /// </summary>
    public ServiceProviderSetting ToSetting(int order)
    {
        return new ServiceProviderSetting
        {
            Id = Id,
            DisplayName = DisplayName,
            Description = Description,
            Category = Category,
            IsEnabled = IsEnabled,
            Order = order
        };
    }
}
