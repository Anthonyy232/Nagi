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
///     A page that displays a grid of all albums from the user's library.
/// </summary>
public sealed partial class AlbumPage : Page
{
    private readonly ILogger<AlbumPage> _logger;
    private CancellationTokenSource? _cancellationTokenSource;
    private bool _isSearchExpanded;

    public AlbumPage()
    {
        InitializeComponent();
        ViewModel = App.Services!.GetRequiredService<AlbumViewModel>();
        _logger = App.Services!.GetRequiredService<ILogger<AlbumPage>>();
        DataContext = ViewModel;

        Loaded += OnPageLoaded;
        _logger.LogDebug("AlbumPage initialized.");
    }

    public AlbumViewModel ViewModel { get; }

    /// <summary>
    ///     Handles the page's navigated-to event.
    ///     Initiates album loading if the collection is empty.
    /// </summary>
    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        try
        {
            base.OnNavigatedTo(e);
            _logger.LogDebug("Navigated to AlbumPage.");
            _cancellationTokenSource = new CancellationTokenSource();

            if (ViewModel.Albums.Count == 0)
            {
                _logger.LogDebug("Album collection is empty, loading albums...");
                try
                {
                    await ViewModel.LoadAlbumsAsync(_cancellationTokenSource.Token);
                    _logger.LogDebug("Successfully loaded albums.");
                }
                catch (TaskCanceledException)
                {
                    _logger.LogDebug("Album loading was cancelled.");
                }
            }
            else
            {
                _logger.LogDebug("Albums already loaded, skipping fetch.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to navigate to AlbumPage correctly.");
        }
    }

    /// <summary>
    ///     Handles the page's navigated-from event.
    ///     Cancels ongoing tasks but does not dispose the Singleton ViewModel.
    /// </summary>
    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        _logger.LogDebug("Navigating away from AlbumPage.");

        if (_cancellationTokenSource is { IsCancellationRequested: false })
        {
            _logger.LogDebug("Cancelling ongoing album loading task.");
            _cancellationTokenSource.Cancel();
        }

        _cancellationTokenSource?.Dispose();
        _cancellationTokenSource = null;
        // Note: ViewModel is Singleton, do not dispose - state persists across navigations
    }

    /// <summary>
    ///     Handles the page loaded event to set initial visual state.
    /// </summary>
    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        _logger.LogDebug("AlbumPage loaded. Setting initial visual state.");
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
        _logger.LogDebug("Search UI expanded.");
        ToolTipService.SetToolTip(SearchToggleButton, Nagi.WinUI.Resources.Strings.AlbumPage_SearchButton_Close_ToolTip);
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
        _logger.LogDebug("Search UI collapsed and search term cleared.");
        ToolTipService.SetToolTip(SearchToggleButton, Nagi.WinUI.Resources.Strings.AlbumPage_SearchButton_Search_ToolTip);
        VisualStateManager.GoToState(this, "SearchCollapsed", true);
        ViewModel.SearchTerm = string.Empty;
    }

    /// <summary>
    ///     Handles clicks on an album item in the grid.
    ///     Navigates to the detailed view for the selected album.
    /// </summary>
    private void AlbumsGridView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is AlbumViewModelItem clickedAlbum)
        {
            _logger.LogDebug(
                "User clicked on album '{AlbumTitle}' by '{ArtistName}' (Id: {AlbumId}). Navigating to detail view.",
                clickedAlbum.Title, clickedAlbum.ArtistName, clickedAlbum.Id);
            _ = ViewModel.NavigateToAlbumDetailAsync(clickedAlbum);
        }
    }
}