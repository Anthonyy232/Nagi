using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;
using Nagi.WinUI.Navigation;
using Nagi.WinUI.Pages;
using Nagi.WinUI.Services.Abstractions;
using Nagi.Core.Helpers;

namespace Nagi.WinUI.ViewModels;

/// <summary>
///     A display-optimized representation of a genre for the user interface.
/// </summary>
public partial class GenreViewModelItem : ObservableObject
{
    [ObservableProperty] public partial Guid Id { get; set; }

    [ObservableProperty] public partial string Name { get; set; } = string.Empty;

    /// <summary>
    ///     The number of songs in this genre (for sorting purposes).
    /// </summary>
    public int SongCount { get; set; }
}

/// <summary>
///     Manages the state and logic for the genre list page.
/// </summary>
public partial class GenreViewModel : ObservableObject, IDisposable
{
    private const int SearchDebounceDelay = 300;
    private readonly NotifyCollectionChangedEventHandler _collectionChangedHandler;
    private readonly IDispatcherService _dispatcherService;
    private readonly ILibraryService _libraryService;
    private readonly ILogger<GenreViewModel> _logger;
    private readonly IMusicPlaybackService _musicPlaybackService;
    private readonly INavigationService _navigationService;
    private readonly IUISettingsService _settingsService;
    private CancellationTokenSource? _debounceCts;
    private bool _isDisposed;
    private bool _hasSortOrderLoaded;
    private List<GenreViewModelItem> _allGenres = new();

    public GenreViewModel(ILibraryService libraryService, IMusicPlaybackService musicPlaybackService,
        INavigationService navigationService, IUISettingsService settingsService, IDispatcherService dispatcherService, ILogger<GenreViewModel> logger)
    {
        _libraryService = libraryService;
        _musicPlaybackService = musicPlaybackService;
        _navigationService = navigationService;
        _settingsService = settingsService;
        _dispatcherService = dispatcherService;
        _logger = logger;

        // Store the handler in a field so we can reliably unsubscribe from it later.
        _collectionChangedHandler = (s, e) => OnPropertyChanged(nameof(HasGenres));
        Genres.CollectionChanged += _collectionChangedHandler;
        
        UpdateSortOrderText();
    }

    [ObservableProperty] public partial ObservableCollection<GenreViewModelItem> Genres { get; set; } = new();

    [ObservableProperty] public partial bool IsLoading { get; set; }

    [ObservableProperty] public partial bool HasLoadError { get; set; }

    [ObservableProperty] public partial string SearchTerm { get; set; } = string.Empty;

    [ObservableProperty] public partial GenreSortOrder CurrentSortOrder { get; set; } = GenreSortOrder.NameAsc;

    [ObservableProperty] public partial string CurrentSortOrderText { get; set; } = string.Empty;

    partial void OnCurrentSortOrderChanged(GenreSortOrder value) => UpdateSortOrderText();

    private bool IsSearchActive => !string.IsNullOrWhiteSpace(SearchTerm);

    /// <summary>
    ///     Gets a value indicating whether there are any genres to display.
    /// </summary>
    public bool HasGenres => Genres.Any();

    /// <summary>
    ///     Cleans up resources by unsubscribing from event handlers.
    /// </summary>
    public void Dispose()
    {
        if (_isDisposed) return;

        if (Genres != null) Genres.CollectionChanged -= _collectionChangedHandler;
        _debounceCts?.Cancel();
        _debounceCts?.Dispose();
        _debounceCts = null;

        _isDisposed = true;
        GC.SuppressFinalize(this);
    }

    private void UpdateSortOrderText()
    {
        CurrentSortOrderText = SortOrderHelper.GetDisplayName(CurrentSortOrder);
    }

    /// <summary>
    ///     Navigates to the detailed view for the selected genre.
    /// </summary>
    [RelayCommand]
    public void NavigateToGenreDetail(GenreViewModelItem? genre)
    {
        if (genre is null) return;

        var navParam = new GenreViewNavigationParameter
        {
            GenreId = genre.Id,
            GenreName = genre.Name
        };
        _navigationService.Navigate(typeof(GenreViewPage), navParam);
    }

    /// <summary>
    ///     Asynchronously loads all genres from the library.
    /// </summary>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    [RelayCommand]
    public async Task LoadGenresAsync(CancellationToken cancellationToken)
    {
        if (IsLoading) return;

        IsLoading = true;
        HasLoadError = false;

        try
        {
            Task<GenreSortOrder>? sortTask = null;
            if (!_hasSortOrderLoaded)
            {
                sortTask = _settingsService.GetSortOrderAsync<GenreSortOrder>(SortOrderHelper.GenresSortOrderKey);
            }

            var genreModelsTask = _libraryService.GetAllGenresAsync();

            if (sortTask != null)
                await Task.WhenAll(sortTask, genreModelsTask).ConfigureAwait(false);
            else
                await genreModelsTask.ConfigureAwait(false);

            if (sortTask != null)
            {
                CurrentSortOrder = sortTask.Result;
                _hasSortOrderLoaded = true;
            }

            var genreModels = genreModelsTask.Result;
            if (cancellationToken.IsCancellationRequested) return;

            _allGenres = genreModels
                .Select(g => new GenreViewModelItem { Id = g.Id, Name = g.Name, SongCount = g.Songs.Count })
                .ToList();

            // Apply current filter and sort
            ApplyFilter();
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Genre loading was canceled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load genres");
            HasLoadError = true;
            Genres.Clear();
        }
        finally
        {
            IsLoading = false;
        }
    }

    partial void OnSearchTermChanged(string value)
    {
        TriggerDebouncedSearch();
    }

    private void TriggerDebouncedSearch()
    {
        try
        {
            _debounceCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Ignore exception if the CancellationTokenSource has already been disposed.
        }

        _debounceCts = new CancellationTokenSource();
        var token = _debounceCts.Token;

        Task.Run(async () =>
        {
            try
            {
                await Task.Delay(SearchDebounceDelay, token);

                if (token.IsCancellationRequested) return;

                await _dispatcherService.EnqueueAsync(() =>
                {
                    if (!token.IsCancellationRequested)
                        ApplyFilter();
                    return Task.CompletedTask;
                });
            }
            catch (TaskCanceledException)
            {
                _logger.LogDebug("Debounced genre search cancelled");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Debounced genre search failed");
            }
        }, token);
    }

    private void ApplyFilter()
    {
        Genres.Clear();

        IEnumerable<GenreViewModelItem> filtered = _allGenres;
        if (IsSearchActive)
            filtered = _allGenres.Where(g =>
                g.Name?.Contains(SearchTerm, StringComparison.OrdinalIgnoreCase) == true);

        // Apply sort order
        var sorted = CurrentSortOrder switch
        {
            GenreSortOrder.NameDesc => filtered.OrderByDescending(g => g.Name, StringComparer.OrdinalIgnoreCase).ThenBy(g => g.Id),
            GenreSortOrder.SongCountDesc => filtered.OrderByDescending(g => g.SongCount).ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ThenBy(g => g.Id),
            _ => filtered.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ThenBy(g => g.Id)
        };

        foreach (var item in sorted)
            Genres.Add(item);
    }

    /// <summary>
    ///     Changes the sort order and reapplies filtering.
    /// </summary>
    [RelayCommand]
    public Task ChangeSortOrderAsync(string sortOrderString)
    {
        if (Enum.TryParse<GenreSortOrder>(sortOrderString, out var newSortOrder)
            && newSortOrder != CurrentSortOrder)
        {
            CurrentSortOrder = newSortOrder;
            _ = _settingsService.SetSortOrderAsync(SortOrderHelper.GenresSortOrderKey, newSortOrder)
                .ContinueWith(t => _logger.LogError(t.Exception, "Failed to save genre sort order"),
                    TaskContinuationOptions.OnlyOnFaulted);
            ApplyFilter();
        }
        return Task.CompletedTask;
    }

    /// <summary>
    ///     Clears the current queue and starts playing all songs in the selected genre.
    /// </summary>
    [RelayCommand]
    private async Task PlayGenreAsync(Guid genreId)
    {
        if (IsLoading || genreId == Guid.Empty) return;

        try
        {
            await _musicPlaybackService.PlayGenreAsync(genreId);
        }
        catch (Exception ex)
        {
            // This is a critical failure as it directly impacts core user functionality.
            _logger.LogCritical(ex, "Failed to play genre {GenreId}", genreId);
        }
    }

    /// <summary>
    ///     Cleans up search state when navigating away from the page.
    /// </summary>
    public void Cleanup()
    {
        _debounceCts?.Cancel();
        _debounceCts?.Dispose();
        _debounceCts = null;
        SearchTerm = string.Empty;
        _logger.LogDebug("Cleaned up GenreViewModel search resources");
    }
}