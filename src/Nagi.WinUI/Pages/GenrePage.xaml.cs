using System;
using System.Threading;
using System.Threading.Tasks;
using Windows.System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Nagi.WinUI.ViewModels;
using Nagi.Core.Services.Data;

namespace Nagi.WinUI.Pages;

public sealed partial class GenrePage : Page
{
    private readonly ILogger<GenrePage> _logger;
    private CancellationTokenSource? _cancellationTokenSource;
    private bool _isSearchExpanded;

    public GenrePage()
    {
        InitializeComponent();
        ViewModel = App.Services!.GetRequiredService<GenreViewModel>();
        _logger = App.Services!.GetRequiredService<ILogger<GenrePage>>();
        DataContext = ViewModel;

        Loaded += OnPageLoaded;
        _logger.LogDebug("GenrePage initialized.");
    }

    public GenreViewModel ViewModel { get; }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        try
        {
            base.OnNavigatedTo(e);
            _logger.LogDebug("Navigated to GenrePage.");
            var cts = new CancellationTokenSource();
            _cancellationTokenSource = cts;

            await ViewModel.LoadGenresAsync(cts.Token);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to navigate to GenrePage correctly.");
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        _logger.LogDebug("Navigating away from GenrePage.");

        ViewModel.ResetState();
        _cancellationTokenSource?.Cancel();

        _cancellationTokenSource?.Dispose();
        _cancellationTokenSource = null;
        // Note: ViewModel is Singleton, do not dispose - state persists across navigations
    }

    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        _logger.LogDebug("GenrePage loaded. Setting initial visual state.");
        _isSearchExpanded = !string.IsNullOrWhiteSpace(ViewModel.SearchTerm);
        ToolTipService.SetToolTip(SearchToggleButton, _isSearchExpanded
            ? Nagi.WinUI.Resources.Strings.GenrePage_SearchButton_Close_ToolTip
            : Nagi.WinUI.Resources.Strings.GenrePage_SearchButton_Search_ToolTip);
        VisualStateManager.GoToState(this, _isSearchExpanded ? "SearchExpanded" : "SearchCollapsed", false);
        Loaded -= OnPageLoaded;
    }

    private void OnSearchToggleButtonClick(object sender, RoutedEventArgs e)
    {
        if (_isSearchExpanded)
            CollapseSearch();
        else
            ExpandSearch();
    }

    private void OnSearchTextBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            _logger.LogDebug("Escape key pressed in search box. Collapsing search.");
            CollapseSearch();
            e.Handled = true;
        }
    }

    private void ExpandSearch()
    {
        if (_isSearchExpanded) return;

        _isSearchExpanded = true;
        _logger.LogDebug("Search UI expanded.");
        ToolTipService.SetToolTip(SearchToggleButton, Nagi.WinUI.Resources.Strings.GenrePage_SearchButton_Close_ToolTip);
        VisualStateManager.GoToState(this, "SearchExpanded", false);
        SearchTextBox.Focus(FocusState.Programmatic);
    }

    private void CollapseSearch()
    {
        if (!_isSearchExpanded) return;

        _isSearchExpanded = false;
        _logger.LogDebug("Search UI collapsed and search term cleared.");
        ToolTipService.SetToolTip(SearchToggleButton, Nagi.WinUI.Resources.Strings.GenrePage_SearchButton_Search_ToolTip);
        VisualStateManager.GoToState(this, "SearchCollapsed", false);
        SearchToggleButton.Focus(FocusState.Programmatic);
        ViewModel.SearchTerm = string.Empty;
    }

    private void GenresGridView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is GenreSummary clickedGenre)
        {
            _logger.LogDebug("User clicked on genre '{GenreName}'. Navigating to detail view.",
                clickedGenre.Name);
            ViewModel.NavigateToGenreDetail(clickedGenre);
        }
    }
}
