using System;
using Windows.System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Nagi.Core.Models;
using Nagi.WinUI.Navigation;
using Nagi.WinUI.ViewModels;

namespace Nagi.WinUI.Pages;

/// <summary>
///     A page for displaying the list of songs that match a smart playlist's rules.
/// </summary>
public sealed partial class SmartPlaylistSongViewPage : Page
{
    private readonly ILogger<SmartPlaylistSongViewPage> _logger;
    private bool _isSearchExpanded;
    private bool _isUpdatingSelection;

    public SmartPlaylistSongViewPage()
    {
        InitializeComponent();
        ViewModel = App.Services!.GetRequiredService<SmartPlaylistSongListViewModel>();
        _logger = App.Services!.GetRequiredService<ILogger<SmartPlaylistSongViewPage>>();
        DataContext = ViewModel;

        Loaded += OnPageLoaded;
        _logger.LogDebug("SmartPlaylistSongViewPage initialized.");
    }

    public SmartPlaylistSongListViewModel ViewModel { get; }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _logger.LogDebug("Navigated to SmartPlaylistSongViewPage.");

        try
        {
            if (e.Parameter is SmartPlaylistSongViewNavigationParameter navParam)
            {
                _logger.LogDebug("Loading songs for smart playlist '{SmartPlaylistName}' (Id: {SmartPlaylistId}).",
                    navParam.Title,
                    navParam.SmartPlaylistId);
                await ViewModel.InitializeAsync(navParam.Title, navParam.SmartPlaylistId, navParam.CoverImageUri);
            }
            else
            {
                var paramType = e.Parameter?.GetType().Name ?? "null";
                _logger.LogWarning(
                    "Received invalid navigation parameter. Expected '{ExpectedType}', got '{ActualType}'. Initializing with fallback state.",
                    nameof(SmartPlaylistSongViewNavigationParameter), paramType);
                await ViewModel.InitializeAsync("Unknown Smart Playlist", null);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize SmartPlaylistSongViewPage.");
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        _logger.LogDebug("Navigating away from SmartPlaylistSongViewPage. Cleaning up ViewModel.");
        ViewModel.Cleanup();
    }

    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        _logger.LogDebug("SmartPlaylistSongViewPage loaded. Setting initial visual state.");
        VisualStateManager.GoToState(this, "SearchCollapsed", false);
        Loaded -= OnPageLoaded;
    }

    private async void EditRulesButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _logger.LogDebug("Edit rules button clicked.");

            var smartPlaylistId = ViewModel.GetSmartPlaylistId();
            if (!smartPlaylistId.HasValue)
            {
                _logger.LogWarning("Cannot edit rules - no smart playlist ID available.");
                return;
            }

            var smartPlaylistService = App.Services!.GetRequiredService<Core.Services.Abstractions.ISmartPlaylistService>();
            var smartPlaylist = await smartPlaylistService.GetSmartPlaylistByIdAsync(smartPlaylistId.Value);

            if (smartPlaylist == null)
            {
                _logger.LogWarning("Could not find smart playlist with ID {SmartPlaylistId}", smartPlaylistId);
                return;
            }

            var dialog = new Dialogs.SmartPlaylistEditorDialog
            {
                XamlRoot = XamlRoot,
                EditingPlaylist = smartPlaylist
            };

            var result = await dialog.ShowAsync();

            if (result == ContentDialogResult.Primary && dialog.ResultPlaylist != null)
            {
                _logger.LogDebug("User edited smart playlist '{PlaylistName}'.", dialog.ResultPlaylist.Name);
                // Refresh the song list
                await ViewModel.InitializeAsync(dialog.ResultPlaylist.Name, smartPlaylistId);
            }
            else
            {
                _logger.LogDebug("User cancelled smart playlist edit.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error opening smart playlist editor");
        }
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

    private void CollapseSearch()
    {
        if (!_isSearchExpanded) return;

        _isSearchExpanded = false;
        _logger.LogDebug("Search UI collapsed and search term cleared.");
        ToolTipService.SetToolTip(SearchToggleButton, "Search in smart playlist");
        VisualStateManager.GoToState(this, "SearchCollapsed", true);
        ViewModel.SearchTerm = string.Empty;
    }

    private void SongsListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingSelection || sender is not ListView listView) return;

        // Update logical selection state based on individual changes
        foreach (var song in e.AddedItems.OfType<Song>())
            ViewModel.SelectionState.Select(song.Id);

        foreach (var song in e.RemovedItems.OfType<Song>())
            ViewModel.SelectionState.Deselect(song.Id);

        ViewModel.OnSongsSelectionChanged(listView.SelectedItems);
    }

    private void OnSongsListViewContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.Item is not Song song) return;

        // Ensure the visual selection state matches our logical state as containers are reused.
        try
        {
            _isUpdatingSelection = true;
            args.ItemContainer.IsSelected = ViewModel.SelectionState.IsSelected(song.Id);
        }
        finally
        {
            _isUpdatingSelection = false;
        }
    }

    private void OnSelectAllAcceleratorInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        // Don't hijack selection if a text input control is focused.
        var focused = FocusManager.GetFocusedElement(this.XamlRoot);
        if (focused is TextBox or PasswordBox or RichEditBox) return;

        _logger.LogDebug("Ctrl+A invoked. Selecting all songs in smart playlist.");
        ViewModel.SelectAllCommand.Execute(null);

        // Sync the current visible items
        try
        {
            _isUpdatingSelection = true;
            SongsListView.SelectAll();
        }
        finally
        {
            _isUpdatingSelection = false;
        }

        args.Handled = true;
    }

    private void SongsListView_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (e.OriginalSource is FrameworkElement { DataContext: Song tappedSong })
        {
            _logger.LogDebug("User double-tapped song '{SongTitle}' (Id: {SongId}). Executing play command.",
                tappedSong.Title, tappedSong.Id);
            ViewModel.PlaySongCommand.Execute(tappedSong);
        }
    }

    private void SongItemMenuFlyout_Opening(object sender, object e)
    {
        if (sender is not MenuFlyout { Target.DataContext: Song rightClickedSong }) return;
        _logger.LogDebug("Context menu opening for song '{SongTitle}'.", rightClickedSong.Title);
        if (!SongsListView.SelectedItems.Contains(rightClickedSong))
            SongsListView.SelectedItem = rightClickedSong;
    }
}
