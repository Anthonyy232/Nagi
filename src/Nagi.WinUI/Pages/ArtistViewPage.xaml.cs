using CommunityToolkit.WinUI;
using System.Numerics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Nagi.Presentation.Services.Implementations;
using Nagi.Presentation.ViewModels;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace Nagi.WinUI.Pages;

public sealed partial class ArtistViewPage : SongListPage
{
    private bool _isUpdatingAlbumsLayout;
    private bool _pendingLayoutUpdate;
    private ScrollViewer? _songsScrollViewer;
    private double _lastKnownAlbumsHeight;

    public ArtistViewPage()
    {
        InitializeComponent();
        ViewModel = App.Services!.GetRequiredService<ViewModelFactory>().Create<ArtistViewViewModel>();
        DataContext = ViewModel;
        InitializeSongList(ViewModel, ColumnHeaderHost, SongsListView);
        SongsListView.Loaded += OnSongsListViewLoaded;
    }

    public ArtistViewViewModel ViewModel { get; }


    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        if (_songsScrollViewer != null)
        {
            _songsScrollViewer.ViewChanged -= OnSongsScrollViewerViewChanged;
        }
    }

    private void AlbumGridView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ArtistAlbumViewModelItem clickedAlbum) ViewModel.OpenAlbum(clickedAlbum);
    }

    private void ArtistImage_PointerEntered(object sender, PointerRoutedEventArgs e) => EditOverlay.Opacity = 1;

    private void ArtistImage_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (EditOverlay.FocusState != FocusState.Keyboard) EditOverlay.Opacity = 0;
    }

    private void EditOverlay_GotFocus(object sender, RoutedEventArgs e)
    {
        if (EditOverlay.FocusState == FocusState.Keyboard) EditOverlay.Opacity = 1;
    }

    private void EditOverlay_LostFocus(object sender, RoutedEventArgs e) => EditOverlay.Opacity = 0;

    private void OnSongsListViewLoaded(object sender, RoutedEventArgs e)
    {
        if (_songsScrollViewer != null) return;

        _songsScrollViewer = SongsListView.FindDescendant<ScrollViewer>();
        if (_songsScrollViewer != null)
        {
            _songsScrollViewer.ViewChanged += OnSongsScrollViewerViewChanged;
        }
    }

    private void OnAlbumsContentSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (Math.Abs(e.NewSize.Height - _lastKnownAlbumsHeight) < 0.5) return;

        _lastKnownAlbumsHeight = e.NewSize.Height;
        ScheduleAlbumsLayoutUpdate();
    }

    private void OnSongsScrollViewerViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        ScheduleAlbumsLayoutUpdate();
    }

    private void ScheduleAlbumsLayoutUpdate()
    {
        // Defer changes until after measure/arrange and coalesce repeated scroll/size events.

        if (_pendingLayoutUpdate) return;
        _pendingLayoutUpdate = true;

        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            _pendingLayoutUpdate = false;
            UpdateAlbumsLayout();
        });
    }

    private void UpdateAlbumsLayout()
    {
        if (_isUpdatingAlbumsLayout || _songsScrollViewer == null || !ViewModel.HasAlbums) return;

        try
        {
            _isUpdatingAlbumsLayout = true;

            var scrollOffset = _songsScrollViewer.VerticalOffset;
            var newHeight = Math.Max(0, _lastKnownAlbumsHeight - scrollOffset);

            // Only apply height if it changed significantly to avoid layout cycles
            if (Math.Abs(AlbumsViewport.Height - newHeight) > 0.5 || double.IsNaN(AlbumsViewport.Height))
            {
                AlbumsViewport.Height = newHeight;
            }

            var translationY = (float)-Math.Min(scrollOffset, _lastKnownAlbumsHeight);
            AlbumsContent.Translation = new Vector3(0, translationY, 0);

            if (_lastKnownAlbumsHeight > 0)
            {
                var opacity = Math.Clamp(newHeight / _lastKnownAlbumsHeight, 0, 1);

                if (opacity < 0.05) opacity = 0;

                AlbumsViewport.Opacity = opacity;
            }
        }
        finally
        {
            _isUpdatingAlbumsLayout = false;
        }
    }
}
