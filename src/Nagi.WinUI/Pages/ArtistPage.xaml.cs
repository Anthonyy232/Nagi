using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using System.Threading;
using Nagi.WinUI.ViewModels;

namespace Nagi.WinUI.Pages;

/// <summary>
/// A page that displays a grid of all artists from the user's library.
/// This page is responsible for creating and managing the lifecycle of its ViewModel.
/// </summary>
public sealed partial class ArtistPage : Page {
    /// <summary>
    /// Gets the view model associated with this page.
    /// </summary>
    public ArtistViewModel ViewModel { get; }

    private CancellationTokenSource? _cancellationTokenSource;

    public ArtistPage() {
        InitializeComponent();
        // Resolve the transient ViewModel from the dependency injection container.
        ViewModel = App.Services!.GetRequiredService<ArtistViewModel>();
        // Set the DataContext for XAML bindings.
        DataContext = ViewModel;
    }

    /// <summary>
    /// Handles the page's navigated-to event.
    /// Initiates artist loading if the collection is empty.
    /// </summary>
    protected override async void OnNavigatedTo(NavigationEventArgs e) {
        base.OnNavigatedTo(e);
        // Create a new CancellationTokenSource for the data loading operation.
        _cancellationTokenSource = new CancellationTokenSource();

        // Load data only if it hasn't been loaded before to avoid unnecessary re-fetching.
        if (ViewModel.Artists.Count == 0) {
            await ViewModel.LoadArtistsAsync(_cancellationTokenSource.Token);
        }
    }

    /// <summary>
    /// Handles the page's navigated-from event.
    /// This is the critical cleanup step. It cancels any ongoing data loading
    /// and disposes the ViewModel to prevent memory leaks.
    /// </summary>
    protected override void OnNavigatedFrom(NavigationEventArgs e) {
        base.OnNavigatedFrom(e);

        // Cancel any background loading tasks associated with this page.
        _cancellationTokenSource?.Cancel();
        _cancellationTokenSource?.Dispose();
        ViewModel.Dispose();
    }

    /// <summary>
    /// Handles clicks on an artist item in the grid.
    /// Navigates to the detailed view for the selected artist by invoking the ViewModel's command.
    /// </summary>
    private void ArtistsGridView_ItemClick(object sender, ItemClickEventArgs e) {
        if (e.ClickedItem is ArtistViewModelItem clickedArtist) {
            ViewModel.NavigateToArtistDetail(clickedArtist);
        }
    }
}