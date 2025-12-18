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

namespace Nagi.WinUI.Pages;

/// <summary>
///     A page that displays a list of all genres from the user's library.
/// </summary>
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
        _logger.LogInformation("GenrePage initialized.");
    }

    public GenreViewModel ViewModel { get; }

    /// <summary>
    ///     Handles the page's navigated-to event. Initiates genre loading if the list is empty.
    /// </summary>
    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _logger.LogInformation("Navigated to GenrePage.");
        _cancellationTokenSource = new CancellationTokenSource();

        if (ViewModel.Genres.Count == 0)
        {
            _logger.LogInformation("Genre collection is empty, loading genres...");
            try
            {
                await ViewModel.LoadGenresAsync(_cancellationTokenSource.Token);
                _logger.LogInformation("Successfully loaded genres.");
            }
            catch (TaskCanceledException)
            {
                _logger.LogInformation("Genre loading was cancelled.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An unexpected error occurred while loading genres.");
            }
        }
        else
        {
            _logger.LogInformation("Genres already loaded, skipping fetch.");
        }
    }

    /// <summary>
    ///     Handles the page's navigated-from event. Cancels any ongoing data loading operations
    ///     and disposes the ViewModel to prevent memory leaks.
    /// </summary>
    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        _logger.LogInformation("Navigating away from GenrePage.");

        if (_cancellationTokenSource is { IsCancellationRequested: false })
        {
            _logger.LogDebug("Cancelling ongoing genre loading task.");
            _cancellationTokenSource.Cancel();
        }

        _cancellationTokenSource?.Dispose();
        _cancellationTokenSource = null;

        ViewModel.Cleanup();
        _logger.LogDebug("Disposing GenreViewModel.");
        ViewModel.Dispose();
    }

    /// <summary>
    ///     Handles the page loaded event to set initial visual state.
    /// </summary>
    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        _logger.LogDebug("GenrePage loaded. Setting initial visual state.");
        VisualStateManager.GoToState(this, "SearchCollapsed", false);
        Loaded -= OnPageLoaded;
    }

    /// <summary>
    ///     Handles the search toggle button click to expand or collapse the search box.
    /// </summary>
    private void OnSearchToggleButtonClick(object sender, RoutedEventArgs e)
    {
        if (_isSearchExpanded)
            CollapseSearch();
        else
            ExpandSearch();
    }

    /// <summary>
    ///     Handles key down events in the search text box.
    /// </summary>
    private void OnSearchTextBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            _logger.LogDebug("Escape key pressed in search box. Collapsing search.");
            CollapseSearch();
            e.Handled = true;
        }
    }

    /// <summary>
    ///     Expands the search interface with an animation.
    /// </summary>
    private void ExpandSearch()
    {
        if (_isSearchExpanded) return;

        _isSearchExpanded = true;
        _logger.LogInformation("Search UI expanded.");
        ToolTipService.SetToolTip(SearchToggleButton, "Close search");
        VisualStateManager.GoToState(this, "SearchExpanded", true);

        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(150);
        timer.Tick += (s, args) =>
        {
            timer.Stop();
            SearchTextBox.Focus(FocusState.Programmatic);
        };
        timer.Start();
    }

    /// <summary>
    ///     Collapses the search interface with an animation and resets the filter.
    /// </summary>
    private void CollapseSearch()
    {
        if (!_isSearchExpanded) return;

        _isSearchExpanded = false;
        _logger.LogInformation("Search UI collapsed and search term cleared.");
        ToolTipService.SetToolTip(SearchToggleButton, "Search genres");
        VisualStateManager.GoToState(this, "SearchCollapsed", true);
        ViewModel.SearchTerm = string.Empty;
    }

    /// <summary>
    ///     Handles clicks on a genre item in the grid, navigating to the detailed view for that genre.
    /// </summary>
    private void GenresGridView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is GenreViewModelItem clickedGenre)
        {
            _logger.LogInformation("User clicked on genre '{GenreName}'. Navigating to detail view.",
                clickedGenre.Name);
            ViewModel.NavigateToGenreDetail(clickedGenre);
        }
    }
}