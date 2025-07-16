using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Nagi.Navigation;
using Nagi.ViewModels;
using System.Threading;

namespace Nagi.Pages;

/// <summary>
/// A page that displays a list of all genres from the user's library.
/// </summary>
public sealed partial class GenrePage : Page {
    private CancellationTokenSource? _cancellationTokenSource;

    public GenreViewModel ViewModel { get; }

    public GenrePage() {
        InitializeComponent();
        ViewModel = App.Services!.GetRequiredService<GenreViewModel>();
    }

    /// <summary>
    /// Handles the page's navigated-to event. Initiates genre loading if the list is empty.
    /// </summary>
    protected override async void OnNavigatedTo(NavigationEventArgs e) {
        base.OnNavigatedTo(e);
        _cancellationTokenSource = new CancellationTokenSource();

        if (ViewModel.Genres.Count == 0) {
            await ViewModel.LoadGenresAsync(_cancellationTokenSource.Token);
        }
    }

    /// <summary>
    /// Handles the page's navigated-from event. Cancels any ongoing data loading operations.
    /// </summary>
    protected override void OnNavigatedFrom(NavigationEventArgs e) {
        base.OnNavigatedFrom(e);
        _cancellationTokenSource?.Cancel();
        _cancellationTokenSource?.Dispose();
        _cancellationTokenSource = null;
    }

    /// <summary>
    /// Handles clicks on a genre item in the grid, navigating to the detailed view for that genre.
    /// </summary>
    private void GenresGridView_ItemClick(object sender, ItemClickEventArgs e) {
        if (e.ClickedItem is GenreViewModelItem clickedGenre) {
            var navParam = new GenreViewNavigationParameter {
                GenreId = clickedGenre.Id,
                GenreName = clickedGenre.Name
            };
            Frame.Navigate(typeof(GenreViewPage), navParam);
        }
    }
}