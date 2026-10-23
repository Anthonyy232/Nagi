using System.Windows.Input;
using CommunityToolkit.WinUI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Nagi.Core.Models;
using Nagi.Presentation.Models;
using Nagi.Presentation.ViewModels;
using Nagi.WinUI.Helpers;

namespace Nagi.WinUI.Controls;

/// <summary>
///     A row of a song list with its context menu, shared by every song list page.
/// </summary>
public sealed partial class SongRow : UserControl
{
    public static readonly DependencyProperty SongProperty = DependencyProperty.Register(
        nameof(Song), typeof(Song), typeof(SongRow), new PropertyMetadata(null));

    public static readonly DependencyProperty ViewModelProperty = DependencyProperty.Register(
        nameof(ViewModel), typeof(SongListViewModelBase), typeof(SongRow), new PropertyMetadata(null));

    public static readonly DependencyProperty ColumnsProperty = DependencyProperty.Register(
        nameof(Columns), typeof(SongListColumns), typeof(SongRow),
        new PropertyMetadata(SongListColumns.None, (d, _) => ((SongRow)d).ApplyColumns()));

    public static readonly DependencyProperty ShowDiscNumberProperty = DependencyProperty.Register(
        nameof(ShowDiscNumber), typeof(bool), typeof(SongRow), new PropertyMetadata(true));

    public static readonly DependencyProperty ShowGoToAlbumProperty = DependencyProperty.Register(
        nameof(ShowGoToAlbum), typeof(bool), typeof(SongRow), new PropertyMetadata(true));

    public static readonly DependencyProperty ShowGoToArtistProperty = DependencyProperty.Register(
        nameof(ShowGoToArtist), typeof(bool), typeof(SongRow), new PropertyMetadata(true));

    public static readonly DependencyProperty RemoveFromPlaylistCommandProperty = DependencyProperty.Register(
        nameof(RemoveFromPlaylistCommand), typeof(ICommand), typeof(SongRow), new PropertyMetadata(null));

    public SongRow()
    {
        InitializeComponent();
        ApplyColumns();
    }

    public Song? Song
    {
        get => (Song?)GetValue(SongProperty);
        set => SetValue(SongProperty, value);
    }

    public SongListViewModelBase? ViewModel
    {
        get => (SongListViewModelBase?)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    public SongListColumns Columns
    {
        get => (SongListColumns)GetValue(ColumnsProperty);
        set => SetValue(ColumnsProperty, value);
    }

    /// <summary>
    ///     Whether the track column includes the disc number. Lists grouped under disc headers turn it off.
    /// </summary>
    public bool ShowDiscNumber
    {
        get => (bool)GetValue(ShowDiscNumberProperty);
        set => SetValue(ShowDiscNumberProperty, value);
    }

    public bool ShowGoToAlbum
    {
        get => (bool)GetValue(ShowGoToAlbumProperty);
        set => SetValue(ShowGoToAlbumProperty, value);
    }

    public bool ShowGoToArtist
    {
        get => (bool)GetValue(ShowGoToArtistProperty);
        set => SetValue(ShowGoToArtistProperty, value);
    }

    /// <summary>
    ///     Adds "Remove from playlist" to the menu when set.
    /// </summary>
    public ICommand? RemoveFromPlaylistCommand
    {
        get => (ICommand?)GetValue(RemoveFromPlaylistCommandProperty);
        set => SetValue(RemoveFromPlaylistCommandProperty, value);
    }

    private string GetTrackText(Song? song, bool showDiscNumber) =>
        (showDiscNumber ? song?.TrackDisplay : song?.TrackNumber?.ToString()) ?? string.Empty;

    // A hidden cell collapses its Auto column, but a star column keeps its share and MinWidth.
    private void ApplyColumns()
    {
        var columns = Columns;
        TrackText.Visibility = ToVisibility(columns.HasFlag(SongListColumns.TrackNumber));
        SetStarColumn(ArtistColumn, ArtistCell, columns.HasFlag(SongListColumns.Artist));
        SetStarColumn(AlbumColumn, AlbumLink, columns.HasFlag(SongListColumns.Album));
        YearText.Visibility = ToVisibility(columns.HasFlag(SongListColumns.Year));
        DurationText.Visibility = ToVisibility(columns.HasFlag(SongListColumns.Duration));
    }

    private static void SetStarColumn(ColumnDefinition column, FrameworkElement cell, bool isVisible)
    {
        column.Width = isVisible ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        column.MinWidth = isVisible ? 112 : 0;
        cell.Visibility = ToVisibility(isVisible);
    }

    private static Visibility ToVisibility(bool isVisible) => isVisible ? Visibility.Visible : Visibility.Collapsed;

    // Keeps the artist links from growing past their column, so long multi-artist names trim.
    private void ArtistCell_SizeChanged(object sender, SizeChangedEventArgs e) => ArtistLinks.MaxWidth = e.NewSize.Width;

    private void SongMenu_Opening(object sender, object e)
    {
        var viewModel = ViewModel!;

        // Right-clicking a row outside the selection acts on that row alone.
        if (this.FindAscendant<ListViewItem>() is { } container
            && ItemsControl.ItemsControlFromItemContainer(container) is ListViewBase list
            && !list.SelectedItems.Contains(container.Content))
            list.SelectedItem = container.Content;

        var isSingle = viewModel.IsSingleSongSelected;
        SingleSongSeparator.Visibility = ToVisibility(isSingle);
        GoToAlbumItem.Visibility = ToVisibility(isSingle && ShowGoToAlbum);
        GoToArtistSubMenu.Visibility = ToVisibility(isSingle && ShowGoToArtist);
        ShowInExplorerItem.Visibility = ToVisibility(isSingle);
        SongInfoItem.Visibility = ToVisibility(isSingle);
        RemoveFromPlaylistSeparator.Visibility = ToVisibility(RemoveFromPlaylistCommand is not null);
        RemoveFromPlaylistItem.Visibility = ToVisibility(RemoveFromPlaylistCommand is not null);

        PlaylistMenuFlyoutHelper.Populate(AddToPlaylistSubMenu, viewModel.AvailablePlaylists,
            viewModel.AddSelectedSongsToPlaylistCommand);
        if (isSingle && ShowGoToArtist)
            ArtistMenuFlyoutHelper.PopulateSubMenu(GoToArtistSubMenu, Song!, viewModel.GoToArtistCommand,
                viewModel.GetArtistsForSongAsync, DispatcherQueue,
                App.Services!.GetRequiredService<ILogger<SongRow>>());
        ListeningMenuFlyoutHelper.PopulateFavorites((MenuFlyout)sender, viewModel);
    }
}
