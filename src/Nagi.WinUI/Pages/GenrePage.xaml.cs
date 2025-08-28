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
///     A page that displays a list of all genres from the user's library.
/// </summary>
public sealed partial class GenrePage : Page {
    private readonly ILogger<GenrePage> _logger;
    private CancellationTokenSource? _cancellationTokenSource;

    public GenrePage() {
        InitializeComponent();
        ViewModel = App.Services!.GetRequiredService<GenreViewModel>();
        _logger = App.Services!.GetRequiredService<ILogger<GenrePage>>();
        DataContext = ViewModel;
        _logger.LogInformation("GenrePage initialized.");
    }

    public GenreViewModel ViewModel { get; }

    /// <summary>
    ///     Handles the page's navigated-to event. Initiates genre loading if the list is empty.
    /// </summary>
    protected override async void OnNavigatedTo(NavigationEventArgs e) {
        base.OnNavigatedTo(e);
        _logger.LogInformation("Navigated to GenrePage.");
        _cancellationTokenSource = new CancellationTokenSource();

        if (ViewModel.Genres.Count == 0) {
            _logger.LogInformation("Genre collection is empty, loading genres...");
            try {
                await ViewModel.LoadGenresAsync(_cancellationTokenSource.Token);
                _logger.LogInformation("Successfully loaded genres.");
            }
            catch (TaskCanceledException) {
                _logger.LogInformation("Genre loading was cancelled.");
            }
            catch (Exception ex) {
                _logger.LogError(ex, "An unexpected error occurred while loading genres.");
            }
        }
        else {
            _logger.LogInformation("Genres already loaded, skipping fetch.");
        }
    }

    /// <summary>
    ///     Handles the page's navigated-from event. Cancels any ongoing data loading operations
    ///     and disposes the ViewModel to prevent memory leaks.
    /// </summary>
    protected override void OnNavigatedFrom(NavigationEventArgs e) {
        base.OnNavigatedFrom(e);
        _logger.LogInformation("Navigating away from GenrePage.");

        if (_cancellationTokenSource is { IsCancellationRequested: false }) {
            _logger.LogDebug("Cancelling ongoing genre loading task.");
            _cancellationTokenSource.Cancel();
        }

        _cancellationTokenSource?.Dispose();
        _cancellationTokenSource = null;

        _logger.LogDebug("Disposing GenreViewModel.");
        ViewModel.Dispose();
    }

    /// <summary>
    ///     Handles clicks on a genre item in the grid, navigating to the detailed view for that genre.
    /// </summary>
    private void GenresGridView_ItemClick(object sender, ItemClickEventArgs e) {
        if (e.ClickedItem is GenreViewModelItem clickedGenre) {
            _logger.LogInformation("User clicked on genre '{GenreName}'. Navigating to detail view.", clickedGenre.Name);
            ViewModel.NavigateToGenreDetail(clickedGenre);
        }
    }
}