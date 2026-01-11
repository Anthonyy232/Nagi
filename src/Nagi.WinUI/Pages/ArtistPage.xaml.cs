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
using Windows.Storage.Pickers;
using WinRT.Interop;
using Nagi.Core.Constants;

namespace Nagi.WinUI.Pages;

/// <summary>
///     A page that displays a grid of all artists from the user's library.
///     This page is responsible for creating and managing the lifecycle of its ViewModel.
/// </summary>
public sealed partial class ArtistPage : Page
{
    private readonly ILogger<ArtistPage> _logger;
    private CancellationTokenSource? _cancellationTokenSource;
    private bool _isSearchExpanded;

    public ArtistPage()
    {
        InitializeComponent();
        ViewModel = App.Services!.GetRequiredService<ArtistViewModel>();
        _logger = App.Services!.GetRequiredService<ILogger<ArtistPage>>();
        DataContext = ViewModel;

        Loaded += OnPageLoaded;
        _logger.LogDebug("ArtistPage initialized.");
    }

    /// <summary>
    ///     Gets the view model associated with this page.
    /// </summary>
    public ArtistViewModel ViewModel { get; }

    /// <summary>
    ///     Handles the page's navigated-to event.
    ///     Initiates artist loading if the collection is empty.
    /// </summary>
    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _logger.LogDebug("Navigated to ArtistPage.");
        _cancellationTokenSource = new CancellationTokenSource();
        ViewModel.SubscribeToEvents();

        if (ViewModel.Artists.Count == 0)
        {
            _logger.LogDebug("Artist collection is empty, loading artists...");
            try
            {
                await ViewModel.LoadArtistsAsync(_cancellationTokenSource.Token);
                _logger.LogDebug("Successfully loaded artists.");
            }
            catch (TaskCanceledException)
            {
                _logger.LogDebug("Artist loading was cancelled.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An unexpected error occurred while loading artists.");
            }
        }
        else
        {
            _logger.LogDebug("Artists already loaded, skipping fetch.");
        }
    }

    /// <summary>
    ///     Handles the page's navigated-from event.
    ///     This is the critical cleanup step. It cancels any ongoing data loading
    ///     and disposes the ViewModel to prevent memory leaks.
    /// </summary>
    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        _logger.LogDebug("Navigating away from ArtistPage.");
        ViewModel.UnsubscribeFromEvents();

        if (_cancellationTokenSource is { IsCancellationRequested: false })
        {
            _logger.LogDebug("Cancelling ongoing artist loading task.");
            _cancellationTokenSource.Cancel();
        }

        _cancellationTokenSource?.Dispose();
        _cancellationTokenSource = null;

        ViewModel.Cleanup();
        _logger.LogDebug("Disposing ArtistViewModel.");
        ViewModel.Dispose();
    }

    /// <summary>
    ///     Handles the page loaded event to set initial visual state.
    /// </summary>
    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        _logger.LogDebug("ArtistPage loaded. Setting initial visual state.");
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
        _logger.LogDebug("Search UI collapsed and search term cleared.");
        ToolTipService.SetToolTip(SearchToggleButton, "Search artists");
        VisualStateManager.GoToState(this, "SearchCollapsed", true);
        ViewModel.SearchTerm = string.Empty;
    }

    /// <summary>
    ///     Handles clicks on an artist item in the grid.
    ///     Navigates to the detailed view for the selected artist by invoking the ViewModel's command.
    /// </summary>
    private void ArtistsGridView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ArtistViewModelItem clickedArtist)
        {
            _logger.LogDebug(
                "User clicked on artist '{ArtistName}' (Id: {ArtistId}). Navigating to detail view.",
                clickedArtist.Name, clickedArtist.Id);
            ViewModel.NavigateToArtistDetail(clickedArtist);
        }
    }

    private async void ChangeImage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ArtistViewModelItem artistItem }) return;

        try
        {
            _logger.LogDebug("User initiated image change for artist '{ArtistName}'.", artistItem.Name);
            var newImagePath = await PickImageAsync();

            if (!string.IsNullOrWhiteSpace(newImagePath))
            {
                _logger.LogDebug("User selected new image for artist '{ArtistName}'. Updating.", artistItem.Name);
                await ViewModel.UpdateArtistImageCommand.ExecuteAsync(new Tuple<Guid, string>(artistItem.Id, newImagePath));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error changing image for artist {ArtistName}", artistItem.Name);
        }
    }

    private async void RemoveImage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ArtistViewModelItem artistItem }) return;

        try
        {
            _logger.LogDebug("User requested removal of custom image for artist '{ArtistName}'.", artistItem.Name);
            await ViewModel.RemoveArtistImageCommand.ExecuteAsync(artistItem.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error removing image for artist {ArtistName}", artistItem.Name);
        }
    }

    private async Task<string?> PickImageAsync()
    {
        _logger.LogDebug("Opening file picker for artist image.");
        var picker = new FileOpenPicker();
        var hwnd = WindowNative.GetWindowHandle(App.RootWindow);
        InitializeWithWindow.Initialize(picker, hwnd);
        
        foreach (var ext in FileExtensions.ImageFileExtensions)
            picker.FileTypeFilter.Add(ext);

        var file = await picker.PickSingleFileAsync();
        if (file != null)
        {
            _logger.LogDebug("User picked image file: {FilePath}", file.Path);
            return file.Path;
        }

        _logger.LogDebug("User did not pick an image file.");
        return null;
    }
}