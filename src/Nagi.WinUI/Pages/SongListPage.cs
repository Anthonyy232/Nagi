using System.Collections.Generic;
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
///     A searchable page of songs. The selection is whatever the list has selected, including the
///     list's own Ctrl+A. Also handles double-click to play and the now-playing indicator; each row's
///     context menu is part of <see cref="Controls.SongRow" />.
/// </summary>
public abstract class SongListPage : SearchablePage
{
    private readonly List<NowPlayingIndicatorBinder> _nowPlayingBinders = new();
    private SongListViewModelBase? _viewModel;
    private ListView[] _songLists = [];
    private bool _ownsViewModel;

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

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        if (_ownsViewModel) _viewModel!.Dispose();
    }

    internal void ClearSelection() => ActiveSongList.SelectedItems.Clear();

    protected void SongsListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListViewBase listView) _viewModel!.OnSongsSelectionChanged(listView.SelectedItems);
    }

    protected void SongsListView_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (e.OriginalSource is FrameworkElement { DataContext: var item } && GetSong(item) is { } song)
            _viewModel!.PlaySongCommand.Execute(song);
    }

    protected void ListeningOptions_Opening(object sender, object e) =>
        ListeningMenuFlyoutHelper.PopulateMixes((MenuFlyout)sender, _viewModel!.PlayMixCommand, _viewModel.PlayForCommand);

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
