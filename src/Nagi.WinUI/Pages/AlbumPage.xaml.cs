using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using System.Threading;
using Nagi.WinUI.Navigation;
using Nagi.WinUI.ViewModels;

namespace Nagi.WinUI.Pages;

/// <summary>
/// A page that displays a grid of all albums from the user's library.
/// </summary>
public sealed partial class AlbumPage : Page {
    public AlbumViewModel ViewModel { get; }
    private CancellationTokenSource? _cancellationTokenSource;

    public AlbumPage() {
        InitializeComponent();
        ViewModel = App.Services!.GetRequiredService<AlbumViewModel>();
    }

    /// <summary>
    /// Handles the page's navigated-to event.
    /// Initiates album loading if the collection is empty.
    /// </summary>
    protected override async void OnNavigatedTo(NavigationEventArgs e) {
        base.OnNavigatedTo(e);
        _cancellationTokenSource = new CancellationTokenSource();

        if (ViewModel.Albums.Count == 0) {
            await ViewModel.LoadAlbumsAsync(_cancellationTokenSource.Token);
        }
    }

    /// <summary>
    /// Handles the page's navigated-from event.
    /// Cancels any ongoing data loading operations to prevent background work.
    /// </summary>
    protected override void OnNavigatedFrom(NavigationEventArgs e) {
        base.OnNavigatedFrom(e);
        _cancellationTokenSource?.Cancel();
        _cancellationTokenSource?.Dispose();
    }

    /// <summary>
    /// Handles clicks on an album item in the grid.
    /// Navigates to the detailed view for the selected album.
    /// </summary>
    private void AlbumsGridView_ItemClick(object sender, ItemClickEventArgs e) {
        if (e.ClickedItem is AlbumViewModelItem clickedAlbum) {
            var navParam = new AlbumViewNavigationParameter {
                AlbumId = clickedAlbum.Id,
                AlbumTitle = clickedAlbum.Title,
                ArtistName = clickedAlbum.ArtistName
            };
            Frame.Navigate(typeof(AlbumViewPage), navParam);
        }
    }
}