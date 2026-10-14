using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Nagi.Core.Models;
using Nagi.Presentation.ViewModels;
using Nagi.WinUI.Helpers;

namespace Nagi.WinUI.Pages;

/// <summary>
///     A searchable page of songs. Keeps the list's selection in step with the view model's selection
///     state as containers are recycled, and handles select-all, double-click to play, the song
///     context menu and the now-playing indicator.
/// </summary>
public abstract class SongListPage : SearchablePage
{
    private readonly List<NowPlayingIndicatorBinder> _nowPlayingBinders = new();
    private SongListViewModelBase? _viewModel;
    private ListView[] _songLists = [];
    private bool _ownsViewModel;
    private ILogger? _logger;
    private bool _isUpdatingSelection;

    /// <summary>
    ///     Call after InitializeComponent. When <paramref name="ownsViewModel" /> is true the view model
    ///     is disposed on navigating away; cached pages with a shared view model pass false.
    /// </summary>
    protected void InitializeSongList(SongListViewModelBase viewModel, bool ownsViewModel, Button searchButton,
        TextBox searchBox, string searchToolTip, params ListView[] songLists)
    {
        InitializeSearch(viewModel, searchButton, searchBox, searchToolTip);
        _viewModel = viewModel;
        _ownsViewModel = ownsViewModel;
        _songLists = songLists;
        _logger = App.Services!.GetRequiredService<ILoggerFactory>().CreateLogger(GetType());
        Loaded += AttachNowPlayingBinders;
        Unloaded += DetachNowPlayingBinders;
    }

    /// <summary>
    ///     The list currently showing the songs.
    /// </summary>
    protected virtual ListView ActiveSongList => _songLists[0];

    /// <summary>
    ///     The song a list row shows, or null for rows that are not songs.
    /// </summary>
    protected virtual Song? GetSong(object? item) => item as Song;

    /// <summary>
    ///     The id the view model's selection state tracks for a list row.
    /// </summary>
    protected virtual Guid? GetSelectionId(object? item) => GetSong(item)?.Id;

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        if (_ownsViewModel) _viewModel!.Dispose();
    }

    internal void ClearSelection()
    {
        _viewModel!.DeselectAll();
        WithoutSelectionEvents(() => ActiveSongList.SelectedItems.Clear());
    }

    protected void SongsListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingSelection || sender is not ListViewBase listView) return;

        foreach (var item in e.AddedItems)
            if (GetSelectionId(item) is { } id) _viewModel!.SelectionState.Select(id);
        foreach (var item in e.RemovedItems)
            if (GetSelectionId(item) is { } id) _viewModel!.SelectionState.Deselect(id);

        _viewModel!.OnSongsSelectionChanged(listView.SelectedItems.Select(GetSong).OfType<Song>());
    }

    protected void OnSongsListViewContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        // Recycled containers keep their old selection, so restore it from the selection state.
        if (GetSelectionId(args.Item) is not { } id) return;
        WithoutSelectionEvents(() => args.ItemContainer.IsSelected = _viewModel!.SelectionState.IsSelected(id));
    }

    protected void OnSelectAllAcceleratorInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        // Leave Ctrl+A to text inputs.
        if (FocusManager.GetFocusedElement(XamlRoot) is TextBox or PasswordBox or RichEditBox) return;

        _viewModel!.SelectAllCommand.Execute(null);
        WithoutSelectionEvents(ActiveSongList.SelectAll);
        args.Handled = true;
    }

    protected void SongsListView_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (e.OriginalSource is FrameworkElement { DataContext: var item } && GetSong(item) is { } song)
            _viewModel!.PlaySongCommand.Execute(song);
    }

    protected void SongItemMenuFlyout_Opening(object sender, object e)
    {
        if (sender is not MenuFlyout menu) return;

        var item = menu.Target?.DataContext;
        var song = GetSong(item);
        // Right-clicking a row outside the selection acts on that row alone.
        if (song is not null && !ActiveSongList.SelectedItems.Contains(item))
            ActiveSongList.SelectedItem = item;

        foreach (var subMenu in menu.Items.OfType<MenuFlyoutSubItem>())
        {
            if (subMenu.Name.StartsWith("AddToPlaylistSubMenu", StringComparison.Ordinal))
                PlaylistMenuFlyoutHelper.Populate(subMenu, _viewModel!.AvailablePlaylists,
                    _viewModel.AddSelectedSongsToPlaylistCommand);
            else if (song is not null && subMenu.Name.StartsWith("GoToArtistSubMenu", StringComparison.Ordinal))
                ArtistMenuFlyoutHelper.PopulateSubMenu(subMenu, song, _viewModel!.GoToArtistCommand,
                    _viewModel.GetArtistsForSongAsync, DispatcherQueue, _logger!);
        }

        ListeningMenuFlyoutHelper.PopulateFavorites(menu, _viewModel!);
    }

    protected void ListeningOptions_Opening(object sender, object e) =>
        ListeningMenuFlyoutHelper.PopulateMixes((MenuFlyout)sender, _viewModel!.PlayMixCommand, _viewModel.PlayForCommand);

    private void WithoutSelectionEvents(Action update)
    {
        _isUpdatingSelection = true;
        try
        {
            update();
        }
        finally
        {
            _isUpdatingSelection = false;
        }
    }

    // Recreated on each load so a cached page coming back gets fresh binders.
    private void AttachNowPlayingBinders(object sender, RoutedEventArgs e)
    {
        if (_nowPlayingBinders.Count > 0) return;
        var playingBrush = (Brush)Application.Current.Resources["AppPrimaryColorBrush"];
        foreach (var list in _songLists)
        {
            var binder = new NowPlayingIndicatorBinder(list, _viewModel!, playingBrush, item => GetSong(item)?.Id);
            binder.Refresh();
            _nowPlayingBinders.Add(binder);
        }
    }

    private void DetachNowPlayingBinders(object sender, RoutedEventArgs e)
    {
        foreach (var binder in _nowPlayingBinders) binder.Dispose();
        _nowPlayingBinders.Clear();
    }
}
