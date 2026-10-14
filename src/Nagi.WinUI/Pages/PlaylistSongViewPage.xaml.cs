using System;
using System.Linq;
using Windows.System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Nagi.Core.Models;
using Nagi.Presentation.Navigation;
using Nagi.Presentation.ViewModels;
using System.Threading.Tasks;
using Nagi.Core.Services.Abstractions;
using Nagi.Presentation.Resources;
using Nagi.Presentation.Services.Abstractions;
using Nagi.WinUI.Helpers;

namespace Nagi.WinUI.Pages;

public sealed partial class PlaylistSongViewPage : SearchablePage
{
    private readonly ILogger<PlaylistSongViewPage> _logger;
    private bool _isUpdatingSelection;
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcherQueue;
    private NowPlayingIndicatorBinder? _nowPlayingBinder;

    public PlaylistSongViewPage()
    {
        InitializeComponent();
        _dispatcherQueue = this.DispatcherQueue;
        ViewModel = App.Services!.GetRequiredService<IViewModelFactory>().Create<PlaylistSongListViewModel>();
        _logger = App.Services!.GetRequiredService<ILogger<PlaylistSongViewPage>>();
        DataContext = ViewModel;
        InitializeSearch(ViewModel, SearchToggleButton, SearchTextBox, Strings.PlaylistSongViewPage_SearchButton_Search_ToolTip);

        Loaded += OnNowPlayingBinderAttach;
        Unloaded += OnNowPlayingBinderDetach;

        _logger.LogDebug("PlaylistSongViewPage initialized.");
    }

    private void OnNowPlayingBinderAttach(object sender, RoutedEventArgs e)
    {
        if (_nowPlayingBinder is not null) return;
        _nowPlayingBinder = new NowPlayingIndicatorBinder(
            SongsListView,
            ViewModel,
            (Brush)Application.Current.Resources["AppPrimaryColorBrush"]);
        _nowPlayingBinder.Refresh();
    }

    private void OnNowPlayingBinderDetach(object sender, RoutedEventArgs e)
    {
        _nowPlayingBinder?.Dispose();
        _nowPlayingBinder = null;
    }

    public PlaylistSongListViewModel ViewModel { get; }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _logger.LogDebug("Navigated to PlaylistSongViewPage.");

        try
        {
            if (e.Parameter is PlaylistSongViewNavigationParameter navParam)
            {
                await ViewModel.InitializeAsync(navParam.Title, navParam.PlaylistId, navParam.CoverImageUri);
            }
            else
            {
                var paramType = e.Parameter?.GetType().Name ?? "null";
                _logger.LogWarning(
                    "Received invalid navigation parameter. Expected '{ExpectedType}', got '{ActualType}'. Initializing with fallback state.",
                    nameof(PlaylistSongViewNavigationParameter), paramType);
                await ViewModel.InitializeAsync(Strings.PlaylistSongViewPage_UnknownPlaylist, null);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize PlaylistSongViewPage.");
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        _logger.LogDebug("Navigating away from PlaylistSongViewPage. Disposing ViewModel.");
        ViewModel.Dispose();
    }

    private void SongsListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingSelection || sender is not ListView listView) return;

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

        _logger.LogDebug("Ctrl+A invoked. Selecting all songs in playlist.");
        ViewModel.SelectAllCommand.Execute(null);

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
        if (sender is not MenuFlyout menuFlyout) return;
        if (menuFlyout.Target?.DataContext is not Song rightClickedSong) return;

        _logger.LogDebug("Context menu opening for song '{SongTitle}'.", rightClickedSong.Title);
        if (!SongsListView.SelectedItems.Contains(rightClickedSong))
            SongsListView.SelectedItem = rightClickedSong;

        if (menuFlyout.Items.OfType<MenuFlyoutSubItem>()
                .FirstOrDefault(item => item.Name == "GoToArtistSubMenu") is { } goToArtistSubMenu)
        {
            ArtistMenuFlyoutHelper.PopulateSubMenu(goToArtistSubMenu, rightClickedSong, ViewModel.GoToArtistCommand, ViewModel.GetArtistsForSongAsync, _dispatcherQueue, _logger);
        }
        ListeningMenuFlyoutHelper.PopulateFavorites(menuFlyout, ViewModel);
    }

    private void SongsListView_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        // WinUI ListView fires Remove+Add events for reorder, not Move events.
        // DragItemsCompleted fires ONCE after the reorder is complete with the affected items.
        if (args.DropResult != Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move)
            return;

        _ = ViewModel.OnSongsReorderedAsync(args.Items.OfType<Song>().ToList());
    }
    private void ListeningOptions_Opening(object sender, object e) =>
        ListeningMenuFlyoutHelper.PopulateMixes((MenuFlyout)sender, ViewModel.PlayMixCommand, ViewModel.PlayForCommand);
}
