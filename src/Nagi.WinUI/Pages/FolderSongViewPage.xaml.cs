using System;
using System.Linq;
using Windows.System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Nagi.WinUI.Models;
using Nagi.WinUI.Navigation;
using Nagi.WinUI.ViewModels;

namespace Nagi.WinUI.Pages;

/// <summary>
///     A page for displaying the list of songs within a specific folder.
/// </summary>
public sealed partial class FolderSongViewPage : Page
{
    private readonly ILogger<FolderSongViewPage> _logger;
    private bool _isSearchExpanded;

    public FolderSongViewPage()
    {
        InitializeComponent();
        ViewModel = App.Services!.GetRequiredService<FolderSongListViewModel>();
        _logger = App.Services!.GetRequiredService<ILogger<FolderSongViewPage>>();
        DataContext = ViewModel;

        Loaded += OnPageLoaded;
    }

    /// <summary>
    ///     Gets the view model associated with this page.
    /// </summary>
    public FolderSongListViewModel ViewModel { get; }

    /// <summary>
    ///     Initializes the view model with navigation parameters when the page is navigated to.
    /// </summary>
    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        try
        {
            if (e.Parameter is FolderSongViewNavigationParameter navParam)
            {
                await ViewModel.InitializeAsync(navParam.Title, navParam.FolderId, navParam.DirectoryPath);
                await ViewModel.LoadAvailablePlaylistsAsync();
            }
            else
            {
                var paramType = e.Parameter?.GetType().Name ?? "null";
                _logger.LogWarning(
                    "Received invalid navigation parameter. Expected '{ExpectedType}', got '{ActualType}'. Initializing with fallback state.",
                    nameof(FolderSongViewNavigationParameter), paramType);
                await ViewModel.InitializeAsync("Unknown Folder", null);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize FolderSongViewPage.");
        }
    }

    /// <summary>
    ///     Cleans up the search state when navigating away from the page.
    /// </summary>
    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        ViewModel.Cleanup();
    }

    /// <summary>
    ///     Handles the page loaded event to set initial visual state.
    /// </summary>
    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
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
        ToolTipService.SetToolTip(SearchToggleButton, "Search library");
        VisualStateManager.GoToState(this, "SearchCollapsed", true);
        ViewModel.SearchTerm = string.Empty;
    }

    /// <summary>
    ///     Handles clicks on folder content items (folders or songs).
    /// </summary>
    private async void FolderContentsListView_ItemClick(object sender, ItemClickEventArgs e)
    {
        try
        {
            if (e.ClickedItem is FolderContentItem contentItem)
                if (contentItem.IsFolder && contentItem.Folder != null)
                    await ViewModel.NavigateToSubfolderCommand.ExecuteAsync(contentItem.Folder);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling folder content item click");
        }
    }

    /// <summary>
    ///     Updates the view model with the current selection from the content list.
    /// </summary>
    private void FolderContentsListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListView listView)
        {
            var selectedSongs = listView.SelectedItems
                .OfType<FolderContentItem>()
                .Where(item => item.IsSong && item.Song != null)
                .Select(item => item.Song!)
                .ToList();

            ViewModel.OnSongsSelectionChanged(selectedSongs);
        }
    }

    /// <summary>
    ///     Handles the double-tapped event on the content list to play songs or navigate folders.
    /// </summary>
    private async void FolderContentsListView_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        try
        {
            if (e.OriginalSource is FrameworkElement { DataContext: FolderContentItem contentItem })
            {
                if (contentItem.IsFolder && contentItem.Folder != null)
                    await ViewModel.NavigateToSubfolderCommand.ExecuteAsync(contentItem.Folder);
                else if (contentItem.IsSong && contentItem.Song != null)
                    ViewModel.PlaySongCommand.Execute(contentItem.Song);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling folder content double tap");
        }
    }

    /// <summary>
    ///     Handles the opening of the context menu for a content item.
    /// </summary>
    private void SongItemMenuFlyout_Opening(object sender, object e)
    {
        if (sender is not MenuFlyout menuFlyout) return;

        if (menuFlyout.Target?.DataContext is FolderContentItem { IsSong: true, Song: not null } contentItem)
        {
            var listView = FindName("FolderContentsListView") as ListView;
            if (listView != null && !listView.SelectedItems.Contains(contentItem)) listView.SelectedItem = contentItem;
        }

        if (menuFlyout.Items.OfType<MenuFlyoutSubItem>()
                .FirstOrDefault(item => item.Name == "AddToPlaylistSubMenu") is { } addToPlaylistSubMenu)
            PopulatePlaylistSubMenu(addToPlaylistSubMenu);
    }

    /// <summary>
    ///     Populates the "Add to playlist" submenu with available playlists from the view model.
    /// </summary>
    private void PopulatePlaylistSubMenu(MenuFlyoutSubItem subMenu)
    {
        subMenu.Items.Clear();
        var availablePlaylists = ViewModel.AvailablePlaylists;

        if (availablePlaylists?.Any() != true)
        {
            subMenu.Items.Add(new MenuFlyoutItem { Text = "No playlists available", IsEnabled = false });
            return;
        }

        foreach (var playlist in availablePlaylists)
        {
            var playlistMenuItem = new MenuFlyoutItem
            {
                Text = playlist.Name,
                Command = ViewModel.AddSelectedSongsToPlaylistCommand,
                CommandParameter = playlist
            };
            subMenu.Items.Add(playlistMenuItem);
        }
    }
}