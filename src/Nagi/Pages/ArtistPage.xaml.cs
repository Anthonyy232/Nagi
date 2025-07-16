using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Nagi.Navigation;
using Nagi.ViewModels;
using System.Threading;

namespace Nagi.Pages;

/// <summary>
/// A page that displays a grid of all artists from the user's library.
/// </summary>
public sealed partial class ArtistPage : Page {
    public ArtistViewModel ViewModel { get; }
    private CancellationTokenSource? _cancellationTokenSource;

    public ArtistPage() {
        InitializeComponent();
        ViewModel = App.Services!.GetRequiredService<ArtistViewModel>();
    }

    /// <summary>
    /// Handles the page's navigated-to event.
    /// Subscribes to view model events and initiates artist loading if needed.
    /// </summary>
    protected override async void OnNavigatedTo(NavigationEventArgs e) {
        base.OnNavigatedTo(e);
        _cancellationTokenSource = new CancellationTokenSource();
        ViewModel.SubscribeToEvents();

        if (ViewModel.Artists.Count == 0) {
            await ViewModel.LoadArtistsAsync(_cancellationTokenSource.Token);
        }
    }

    /// <summary>
    /// Handles the page's navigated-from event.
    /// Cancels data loading and unsubscribes from events to prevent memory leaks.
    /// </summary>
    protected override void OnNavigatedFrom(NavigationEventArgs e) {
        base.OnNavigatedFrom(e);
        _cancellationTokenSource?.Cancel();
        _cancellationTokenSource?.Dispose();
        ViewModel.UnsubscribeFromEvents();
    }

    /// <summary>
    /// Handles clicks on an artist item in the grid.
    /// Navigates to the detailed view for the selected artist.
    /// </summary>
    private void ArtistsGridView_ItemClick(object sender, ItemClickEventArgs e) {
        if (e.ClickedItem is ArtistViewModelItem clickedArtist) {
            var navParam = new ArtistViewNavigationParameter {
                ArtistId = clickedArtist.Id,
                ArtistName = clickedArtist.Name
            };
            Frame.Navigate(typeof(ArtistViewPage), navParam);
        }
    }
}