using System.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Nagi.Presentation.Lists;

/// <summary>
///     What a page-number pager needs from a list view model. Implement it to reuse the shared pager.
/// </summary>
public interface IPagedListViewModel : INotifyPropertyChanged
{
    int CurrentPage { get; }
    int TotalPages { get; }
    PageSizeSelection PageSize { get; }

    int TotalItemCount { get; }
    bool CanPaginate { get; }
    bool HasNextPage { get; }
    bool HasPreviousPage { get; }
    IAsyncRelayCommand NextPageCommand { get; }
    IAsyncRelayCommand PreviousPageCommand { get; }
}
