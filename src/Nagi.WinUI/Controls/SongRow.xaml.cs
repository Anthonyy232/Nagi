using System.Collections.Generic;
using CommunityToolkit.WinUI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Nagi.Core.Library;
using Nagi.Presentation.Lists;
using Nagi.WinUI.Helpers;

namespace Nagi.WinUI.Controls;

/// <summary>
///     A row of a song list with its context menu, shared by every song list page. Created with
///     <c>isHeader</c> it is instead the list's column header, so the two always line up.
/// </summary>
public sealed partial class SongRow : UserControl
{
    public static readonly DependencyProperty SongProperty = DependencyProperty.Register(
        nameof(Song), typeof(Song), typeof(SongRow),
        new PropertyMetadata(null, (d, _) => ((SongRow)d).UpdateTextCells()));

    public static readonly DependencyProperty ViewModelProperty = DependencyProperty.Register(
        nameof(ViewModel), typeof(SongListViewModelBase), typeof(SongRow),
        new PropertyMetadata(null, (d, e) => ((SongRow)d).BindColumns((SongListViewModelBase?)e.NewValue)));

    private static readonly DependencyProperty ColumnsProperty = DependencyProperty.Register(
        nameof(Columns), typeof(SongListColumns), typeof(SongRow),
        new PropertyMetadata(SongListColumns.None, (d, _) => ((SongRow)d).ApplyColumns()));

    public static readonly DependencyProperty ShowDiscNumberProperty = DependencyProperty.Register(
        nameof(ShowDiscNumber), typeof(bool), typeof(SongRow), new PropertyMetadata(true));

    private readonly List<(SongListColumns Column, TextBlock Cell)> _textCells = new();
    // The cells the star columns hide: the title and links in a row, the labels in a header.
    private FrameworkElement _titleCell;
    private FrameworkElement _artistCell;
    private FrameworkElement _albumCell;

    public SongRow() : this(isHeader: false)
    {
    }

    public SongRow(bool isHeader)
    {
        InitializeComponent();
        _titleCell = SongTitle;
        _artistCell = ArtistCell;
        _albumCell = AlbumLink;

        foreach (var column in SongListColumnsHelper.All)
        {
            // Title, artist and album are the star columns in the XAML; the track number goes before them.
            if (column is SongListColumns.Title or SongListColumns.Artist or SongListColumns.Album) continue;
            if (column != SongListColumns.TrackNumber)
                RowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            AddTextCell(column, column == SongListColumns.TrackNumber ? 1 : RowGrid.ColumnDefinitions.Count - 1);
        }

        if (isHeader) BecomeHeader();
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

    private SongListColumns Columns => (SongListColumns)GetValue(ColumnsProperty);

    /// <summary>
    ///     Whether the track column includes the disc number. Lists grouped under disc headers turn it off.
    /// </summary>
    public bool ShowDiscNumber
    {
        get => (bool)GetValue(ShowDiscNumberProperty);
        set => SetValue(ShowDiscNumberProperty, value);
    }

    // The list decides which columns every row shows.
    private void BindColumns(SongListViewModelBase? viewModel)
    {
        if (viewModel is null) ClearValue(ColumnsProperty);
        else SetBinding(ColumnsProperty, new Binding { Source = viewModel, Path = new PropertyPath(nameof(SongListViewModelBase.VisibleColumns)) });
    }

    private static Style SecondaryTextStyle => (Style)Application.Current.Resources["SongListViewPageSecondaryTextBlockStyle"];

    // Fixed widths, not Auto, so the cells line up from row to row and with the header.
    private static (double Width, TextAlignment Alignment) GetCellLayout(SongListColumns column) => column switch
    {
        SongListColumns.TrackNumber => (30, TextAlignment.Center),
        SongListColumns.Year or SongListColumns.Bpm => (40, TextAlignment.Center),
        SongListColumns.Bitrate => (56, TextAlignment.Right),
        SongListColumns.PlayCount => (48, TextAlignment.Right),
        SongListColumns.LastPlayed or SongListColumns.DateAdded or SongListColumns.FileCreatedDate => (84, TextAlignment.Right),
        SongListColumns.Duration => (58, TextAlignment.Right),
        _ => throw new ArgumentOutOfRangeException(nameof(column), column, null)
    };

    private void AddTextCell(SongListColumns column, int gridColumn)
    {
        var (width, alignment) = GetCellLayout(column);
        var cell = new TextBlock
        {
            Width = width,
            Margin = new Thickness(12, 0, 0, 0),
            TextAlignment = alignment,
            Style = SecondaryTextStyle
        };
        Grid.SetColumn(cell, gridColumn);
        RowGrid.Children.Add(cell);
        _textCells.Add((column, cell));
    }

    private void UpdateTextCells()
    {
        foreach (var (column, cell) in _textCells)
            cell.Text = Song is { } song ? SongListColumnsHelper.GetText(column, song, ShowDiscNumber) : string.Empty;
    }

    private void BecomeHeader()
    {
        RowGrid.Height = 32;
        RowGrid.ContextFlyout = null;
        // Hidden but still laid out, so the artwork column keeps its width.
        PlayButton.Opacity = 0;
        PlayButton.IsHitTestVisible = false;
        PlayButton.IsTabStop = false;
        AutomationProperties.SetAccessibilityView(PlayButton, AccessibilityView.Raw);
        SongTitle.Visibility = Visibility.Collapsed;
        ArtistCell.Visibility = Visibility.Collapsed;
        AlbumLink.Visibility = Visibility.Collapsed;
        _titleCell = AddHeaderLabel(2, SongListColumnsHelper.GetLabel(SongListColumns.Title));
        _artistCell = AddHeaderLabel(3, SongListColumnsHelper.GetLabel(SongListColumns.Artist));
        _albumCell = AddHeaderLabel(4, SongListColumnsHelper.GetLabel(SongListColumns.Album));

        foreach (var (column, cell) in _textCells)
        {
            var label = SongListColumnsHelper.GetLabel(column);
            // The track number column is too narrow for a word.
            cell.Text = column == SongListColumns.TrackNumber ? "#" : label;
            ToolTipService.SetToolTip(cell, label);
        }
    }

    private TextBlock AddHeaderLabel(int gridColumn, string text)
    {
        var label = new TextBlock { Text = text, Margin = new Thickness(12, 0, 0, 0), Style = SecondaryTextStyle };
        Grid.SetColumn(label, gridColumn);
        RowGrid.Children.Add(label);
        return label;
    }

    // A hidden cell collapses its Auto column, but a star column keeps its share and MinWidth.
    private void ApplyColumns()
    {
        var columns = Columns;
        SetStarColumn(TitleColumn, _titleCell, columns.HasFlag(SongListColumns.Title), minWidth: 0);
        SetStarColumn(ArtistColumn, _artistCell, columns.HasFlag(SongListColumns.Artist), minWidth: 112);
        SetStarColumn(AlbumColumn, _albumCell, columns.HasFlag(SongListColumns.Album), minWidth: 112);
        foreach (var (column, cell) in _textCells)
            cell.Visibility = ToVisibility(columns.HasFlag(column));
    }

    private static void SetStarColumn(ColumnDefinition column, FrameworkElement cell, bool isVisible, double minWidth)
    {
        column.Width = isVisible ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        column.MinWidth = isVisible ? minWidth : 0;
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
        FavoriteItem.Visibility = ToVisibility(isSingle);
        FavoriteItem.Text = viewModel.SelectedFavoriteActionText;
        FavoritesSubMenu.Visibility = ToVisibility(!isSingle);
        SingleSongSeparator.Visibility = ToVisibility(isSingle);
        GoToAlbumItem.Visibility = ToVisibility(isSingle && viewModel.ShowsGoToAlbum);
        GoToArtistSubMenu.Visibility = ToVisibility(isSingle && viewModel.ShowsGoToArtist);
        ShowInExplorerItem.Visibility = ToVisibility(viewModel.ShowInFolderCommand.CanExecute(Song));
        SongInfoItem.Visibility = ToVisibility(isSingle);
        RemoveFromPlaylistSeparator.Visibility = ToVisibility(viewModel.RemoveSelectionCommand is not null);
        RemoveFromPlaylistItem.Visibility = ToVisibility(viewModel.RemoveSelectionCommand is not null);

        PlaylistMenuFlyoutHelper.Populate(AddToPlaylistSubMenu, viewModel.AvailablePlaylists,
            viewModel.AddSelectedSongsToPlaylistCommand);
        if (isSingle && viewModel.ShowsGoToArtist)
            ArtistMenuFlyoutHelper.PopulateSubMenu(GoToArtistSubMenu, Song!, viewModel.GoToArtistCommand,
                viewModel.GetArtistsForSongAsync, DispatcherQueue);
    }
}
