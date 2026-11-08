using System.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nagi.Presentation.Common;

namespace Nagi.Presentation.Lists;

/// <summary>
///     What a page-number pager needs from a list view model. Implement it to reuse the shared pager.
/// </summary>
public interface IPagedListViewModel : INotifyPropertyChanged
{
    int CurrentPage { get; }
    int TotalPages { get; }
    IReadOnlyList<SettingOption<int>> PageSizeOptions { get; }

    /// <summary>Null when the saved page size isn't one of the options.</summary>
    SettingOption<int>? SelectedPageSize { get; set; }

    int TotalItemCount { get; }
    bool CanPaginate { get; }
    bool HasNextPage { get; }
    bool HasPreviousPage { get; }
    IAsyncRelayCommand NextPageCommand { get; }
    IAsyncRelayCommand PreviousPageCommand { get; }
}
