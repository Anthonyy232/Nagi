using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Nagi.Presentation.ViewModels;
using Nagi.WinUI.Helpers;

namespace Nagi.WinUI.Pages;

/// <summary>
///     A page with the collapsible search box: a toggle button, a text box, and the
///     SearchExpanded and SearchCollapsed visual states.
/// </summary>
public abstract class SearchablePage : Page
{
    private SearchableViewModelBase? _searchViewModel;
    private CollapsibleSearch? _search;

    /// <summary>
    ///     Call after InitializeComponent. Each time the page loads, the box opens if the view model
    ///     still has a search term, so cached pages come back in a consistent state.
    /// </summary>
    protected void InitializeSearch(SearchableViewModelBase viewModel, Button searchButton, TextBox searchBox, string searchToolTip)
    {
        _searchViewModel = viewModel;
        _search = new CollapsibleSearch(this, searchButton, searchBox, searchToolTip, "SearchExpanded", "SearchCollapsed",
            () => viewModel.SearchTerm = string.Empty);
        Loaded += OnSearchablePageLoaded;
    }

    protected void OnSearchToggleButtonClick(object sender, RoutedEventArgs e) => _search!.Toggle();

    protected void OnSearchTextBoxKeyDown(object sender, KeyRoutedEventArgs e) => _search!.HandleKeyDown(e);

    private void OnSearchablePageLoaded(object sender, RoutedEventArgs e) =>
        _search!.SetExpanded(!string.IsNullOrWhiteSpace(_searchViewModel!.SearchTerm));
}
