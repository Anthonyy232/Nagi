using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Nagi.WinUI.ViewModels;

namespace Nagi.WinUI.Pages;

/// <summary>
///     A page that displays a grid of all albums from the user's library.
/// </summary>
public sealed partial class AlbumPage : Page {
    private readonly ILogger<AlbumPage> _logger;
    private CancellationTokenSource? _cancellationTokenSource;

    public AlbumPage() {
        InitializeComponent();
        ViewModel = App.Services!.GetRequiredService<AlbumViewModel>();
        _logger = App.Services!.GetRequiredService<ILogger<AlbumPage>>();
        DataContext = ViewModel;

        _logger.LogInformation("AlbumPage initialized.");
    }

    public AlbumViewModel ViewModel { get; }

    /// <summary>
    ///     Handles the page's navigated-to event.
    ///     Initiates album loading if the collection is empty.
    /// </summary>
    protected override async void OnNavigatedTo(NavigationEventArgs e) {
        base.OnNavigatedTo(e);
        _logger.LogInformation("Navigated to AlbumPage.");
        _cancellationTokenSource = new CancellationTokenSource();

        if (ViewModel.Albums.Count == 0) {
            _logger.LogInformation("Album collection is empty, loading albums...");
            try {
                await ViewModel.LoadAlbumsAsync(_cancellationTokenSource.Token);
                _logger.LogInformation("Successfully loaded albums.");
            }
            catch (TaskCanceledException) {
                _logger.LogInformation("Album loading was cancelled.");
            }
            catch (Exception ex) {
                _logger.LogError(ex, "An unexpected error occurred while loading albums.");
            }
        }
        else {
            _logger.LogInformation("Albums already loaded, skipping fetch.");
        }
    }

    /// <summary>
    ///     Handles the page's navigated-from event.
    ///     Cancels any ongoing data loading operations and disposes the ViewModel.
    /// </summary>
    protected override void OnNavigatedFrom(NavigationEventArgs e) {
        base.OnNavigatedFrom(e);
        _logger.LogInformation("Navigating away from AlbumPage.");

        if (_cancellationTokenSource is { IsCancellationRequested: false }) {
            _logger.LogDebug("Cancelling ongoing album loading task.");
            _cancellationTokenSource.Cancel();
        }

        _cancellationTokenSource?.Dispose();
        _cancellationTokenSource = null;

        _logger.LogDebug("Disposing AlbumViewModel.");
        ViewModel.Dispose();
    }

    /// <summary>
    ///     Handles clicks on an album item in the grid.
    ///     Navigates to the detailed view for the selected album.
    /// </summary>
    private void AlbumsGridView_ItemClick(object sender, ItemClickEventArgs e) {
        if (e.ClickedItem is AlbumViewModelItem clickedAlbum) {
            _logger.LogInformation(
                "User clicked on album '{AlbumTitle}' by '{ArtistName}' (Id: {AlbumId}). Navigating to detail view.",
                clickedAlbum.Title, clickedAlbum.ArtistName, clickedAlbum.Id);
            ViewModel.NavigateToAlbumDetail(clickedAlbum);
        }
    }
}