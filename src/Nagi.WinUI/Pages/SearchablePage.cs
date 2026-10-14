using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Nagi.Presentation.Resources;
using Nagi.Presentation.ViewModels;
using Windows.System;

namespace Nagi.WinUI.Pages;

/// <summary>
///     A page with the collapsible search box: a toggle button, a text box, and the
///     SearchExpanded and SearchCollapsed visual states.
/// </summary>
public abstract class SearchablePage : Page
{
    private SearchableViewModelBase? _searchViewModel;
    private Button? _searchButton;
    private TextBox? _searchBox;
    private string _searchToolTip = string.Empty;
    private bool _isSearchExpanded;

    /// <summary>
    ///     Call after InitializeComponent. Each time the page loads, the box opens if the view model
    ///     still has a search term, so cached pages come back in a consistent state.
    /// </summary>
    protected void InitializeSearch(SearchableViewModelBase viewModel, Button searchButton, TextBox searchBox, string searchToolTip)
    {
        _searchViewModel = viewModel;
        _searchButton = searchButton;
        _searchBox = searchBox;
        _searchToolTip = searchToolTip;
        Loaded += OnSearchablePageLoaded;
    }

    protected void OnSearchToggleButtonClick(object sender, RoutedEventArgs e)
    {
        if (_isSearchExpanded)
            CollapseSearch();
        else
            ExpandSearch();
    }

    protected void OnSearchTextBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Escape) return;
        CollapseSearch();
        e.Handled = true;
    }

    private void OnSearchablePageLoaded(object sender, RoutedEventArgs e)
    {
        SetSearchExpanded(!string.IsNullOrWhiteSpace(_searchViewModel!.SearchTerm));
    }

    private void ExpandSearch()
    {
        if (_isSearchExpanded) return;
        SetSearchExpanded(true);
        _searchBox!.Focus(FocusState.Programmatic);
    }

    private void CollapseSearch()
    {
        if (!_isSearchExpanded) return;
        SetSearchExpanded(false);
        _searchButton!.Focus(FocusState.Programmatic);
        _searchViewModel!.SearchTerm = string.Empty;
    }

    private void SetSearchExpanded(bool expanded)
    {
        _isSearchExpanded = expanded;
        var label = expanded ? Strings.Generic_CloseSearch : _searchToolTip;
        ToolTipService.SetToolTip(_searchButton, label);
        AutomationProperties.SetName(_searchButton, label);
        VisualStateManager.GoToState(this, expanded ? "SearchExpanded" : "SearchCollapsed", false);
    }
}
