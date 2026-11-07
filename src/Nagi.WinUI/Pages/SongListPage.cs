using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Nagi.Core.Models;
using Nagi.Presentation.Navigation;
using Nagi.Presentation.ViewModels;
using Nagi.WinUI.Controls;
using Nagi.WinUI.Helpers;

namespace Nagi.WinUI.Pages;

/// <summary>
///     A page of songs. The selection is whatever the list has selected, including the
///     list's own Ctrl+A. Also handles double-click to play, the now-playing indicator and the column
///     header; each row's context menu is part of <see cref="SongRow" />.
/// </summary>
public abstract class SongListPage : Page
{
    private readonly List<NowPlayingIndicatorBinder> _nowPlayingBinders = new();
    private SongListViewModelBase? _viewModel;
    private ListView[] _songLists = [];

    /// <summary>
    ///     Call after InitializeComponent. The column header goes in <paramref name="columnHeaderHost" /> above the
    ///     lists, not in them, so it stays in view and re-sorting doesn't make a list follow its old first row.
    /// </summary>
    protected void InitializeSongList(SongListViewModelBase viewModel, Border columnHeaderHost, params ListView[] songLists)
    {
        _viewModel = viewModel;
        _songLists = songLists;
        columnHeaderHost.Child = CreateColumnHeader(viewModel);
        Loaded += AttachNowPlayingBinders;
        Unloaded += DetachNowPlayingBinders;
    }

    protected virtual ListView ActiveSongList => _songLists[0];

    /// <summary>
    ///     The song a list row shows, or null for rows that are not songs.
    /// </summary>
    protected virtual Song? GetSong(object? item) => item as Song;

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        // For the "Add to playlist" menu. A cached page's shared view model keeps them up to date once loaded.
        if (!_viewModel!.HasLoadedPlaylists) _ = _viewModel.LoadAvailablePlaylistsAsync();
        if (_viewModel is IDetailListViewModel detail && e.Parameter is INavigationParameter parameter)
            await detail.OpenAsync(parameter);
    }

    // A cached page keeps its view model, which is shared, for the next visit.
    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        if (NavigationCacheMode == NavigationCacheMode.Disabled) _viewModel!.Dispose();
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

    private static SongRow CreateColumnHeader(SongListViewModelBase viewModel)
    {
        var header = new SongRow(isHeader: true) { ViewModel = viewModel };
        header.SetBinding(VisibilityProperty, new Binding
        {
            Source = viewModel,
            Path = new PropertyPath(nameof(SongListViewModelBase.ShowsColumnHeader)),
            Converter = (IValueConverter)Application.Current.Resources["BooleanToVisibilityConverter"]
        });
        return header;
    }

    // Recreated on each load so a cached page coming back gets fresh binders.
    private void AttachNowPlayingBinders(object sender, RoutedEventArgs e)
    {
        if (_nowPlayingBinders.Count > 0) return;
        foreach (var list in _songLists)
            _nowPlayingBinders.Add(new NowPlayingIndicatorBinder(list, _viewModel!, item => GetSong(item)?.Id));
    }

    private void DetachNowPlayingBinders(object sender, RoutedEventArgs e)
    {
        foreach (var binder in _nowPlayingBinders) binder.Dispose();
        _nowPlayingBinders.Clear();
    }
}
