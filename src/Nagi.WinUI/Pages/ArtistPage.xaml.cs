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
///     A page that displays a grid of all artists from the user's library.
///     This page is responsible for creating and managing the lifecycle of its ViewModel.
/// </summary>
public sealed partial class ArtistPage : Page {
    private readonly ILogger<ArtistPage> _logger;
    private CancellationTokenSource? _cancellationTokenSource;

    public ArtistPage() {
        InitializeComponent();
        ViewModel = App.Services!.GetRequiredService<ArtistViewModel>();
        _logger = App.Services!.GetRequiredService<ILogger<ArtistPage>>();
        DataContext = ViewModel;

        _logger.LogInformation("ArtistPage initialized.");
    }

    /// <summary>
    ///     Gets the view model associated with this page.
    /// </summary>
    public ArtistViewModel ViewModel { get; }

    /// <summary>
    ///     Handles the page's navigated-to event.
    ///     Initiates artist loading if the collection is empty.
    /// </summary>
    protected override async void OnNavigatedTo(NavigationEventArgs e) {
        base.OnNavigatedTo(e);
        _logger.LogInformation("Navigated to ArtistPage.");
        _cancellationTokenSource = new CancellationTokenSource();

        if (ViewModel.Artists.Count == 0) {
            _logger.LogInformation("Artist collection is empty, loading artists...");
            try {
                await ViewModel.LoadArtistsAsync(_cancellationTokenSource.Token);
                _logger.LogInformation("Successfully loaded artists.");
            }
            catch (TaskCanceledException) {
                _logger.LogInformation("Artist loading was cancelled.");
            }
            catch (Exception ex) {
                _logger.LogError(ex, "An unexpected error occurred while loading artists.");
            }
        }
        else {
            _logger.LogInformation("Artists already loaded, skipping fetch.");
        }
    }

    /// <summary>
    ///     Handles the page's navigated-from event.
    ///     This is the critical cleanup step. It cancels any ongoing data loading
    ///     and disposes the ViewModel to prevent memory leaks.
    /// </summary>
    protected override void OnNavigatedFrom(NavigationEventArgs e) {
        base.OnNavigatedFrom(e);
        _logger.LogInformation("Navigating away from ArtistPage.");

        if (_cancellationTokenSource is { IsCancellationRequested: false }) {
            _logger.LogDebug("Cancelling ongoing artist loading task.");
            _cancellationTokenSource.Cancel();
        }

        _cancellationTokenSource?.Dispose();
        _cancellationTokenSource = null;

        _logger.LogDebug("Disposing ArtistViewModel.");
        ViewModel.Dispose();
    }

    /// <summary>
    ///     Handles clicks on an artist item in the grid.
    ///     Navigates to the detailed view for the selected artist by invoking the ViewModel's command.
    /// </summary>
    private void ArtistsGridView_ItemClick(object sender, ItemClickEventArgs e) {
        if (e.ClickedItem is ArtistViewModelItem clickedArtist) {
            _logger.LogInformation(
                "User clicked on artist '{ArtistName}' (Id: {ArtistId}). Navigating to detail view.",
                clickedArtist.Name, clickedArtist.Id);
            ViewModel.NavigateToArtistDetail(clickedArtist);
        }
    }
}