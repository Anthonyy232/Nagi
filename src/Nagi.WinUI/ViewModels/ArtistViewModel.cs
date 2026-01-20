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
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;
using Nagi.WinUI.Navigation;
using Nagi.WinUI.Pages;
using Nagi.WinUI.Services.Abstractions;
using Nagi.Core.Helpers;
using Nagi.WinUI.Helpers;

namespace Nagi.WinUI.ViewModels;

/// <summary>
///     A display-optimized representation of an artist for the user interface.
/// </summary>
public partial class ArtistViewModelItem : ObservableObject
{
    [ObservableProperty] public partial Guid Id { get; set; }
    [ObservableProperty] public partial string Name { get; set; } = string.Empty;
    [ObservableProperty] public partial string? LocalImageCachePath { get; set; }

    public bool IsArtworkAvailable => !string.IsNullOrEmpty(LocalImageCachePath);

    public bool IsCustomImage => LocalImageCachePath?.Contains(".custom.") == true;

    partial void OnLocalImageCachePathChanged(string? value)
    {
        OnPropertyChanged(nameof(IsArtworkAvailable));
        OnPropertyChanged(nameof(IsCustomImage));
    }
}

/// <summary>
///     Manages the state and logic for the artist list page, including data fetching and live updates.
/// </summary>
public partial class ArtistViewModel : SearchableViewModelBase, IDisposable
{
    private const int PageSize = 250;
    private readonly Dictionary<Guid, ArtistViewModelItem> _artistLookup = new();
    private readonly NotifyCollectionChangedEventHandler _collectionChangedHandler;
    private readonly ILibraryService _libraryService;
    private readonly IMusicPlaybackService _musicPlaybackService;
    private readonly INavigationService _navigationService;
    private readonly IUISettingsService _settingsService;
    private readonly IMusicNavigationService _musicNavigationService;
    private int _currentPage = 1;
    private bool _isFullyLoaded;
    private bool _hasSortOrderLoaded;
    private bool _isNavigating;

    public ArtistViewModel(
        ILibraryService libraryService,
        IUISettingsService settingsService,
        IMusicPlaybackService musicPlaybackService,
        IDispatcherService dispatcherService,
        INavigationService navigationService,
        IMusicNavigationService musicNavigationService,
        ILogger<ArtistViewModel> logger)
        : base(dispatcherService, logger)
    {
        _libraryService = libraryService;
        _settingsService = settingsService;
        _musicPlaybackService = musicPlaybackService;
        _navigationService = navigationService;
        _musicNavigationService = musicNavigationService;

        // Store the handler in a field so we can reliably unsubscribe from it later.
        _collectionChangedHandler = (s, e) => OnPropertyChanged(nameof(HasArtists));
        Artists.CollectionChanged += _collectionChangedHandler;
        
        UpdateSortOrderText();
    }

    [ObservableProperty] public partial ObservableCollection<ArtistViewModelItem> Artists { get; set; } = new();

    [ObservableProperty] public partial bool IsLoading { get; set; }

    [ObservableProperty] public partial bool IsLoadingMore { get; set; }

    [ObservableProperty] public partial bool HasLoadError { get; set; }

    [ObservableProperty] public partial string TotalItemsText { get; set; } = "0 artists";

    [ObservableProperty] public partial ArtistSortOrder CurrentSortOrder { get; set; } = ArtistSortOrder.NameAsc;

    [ObservableProperty] public partial string CurrentSortOrderText { get; set; } = string.Empty;

    partial void OnCurrentSortOrderChanged(ArtistSortOrder value) => UpdateSortOrderText();


    public bool HasArtists => Artists.Any();

    /// <summary>
    ///     Cleans up resources by unsubscribing from event handlers.
    /// </summary>
    public void Dispose()
    {
        if (_isDisposed) return;

        if (Artists != null) Artists.CollectionChanged -= _collectionChangedHandler;
        _libraryService.ArtistMetadataUpdated -= OnArtistMetadataUpdated;
        CancelPendingSearch();
        _logger.LogDebug("ArtistViewModel state cleaned up.");

        _isDisposed = true;
        GC.SuppressFinalize(this);
    }

    /// <summary>
    ///     Navigates to the detailed view for the selected artist.
    /// </summary>
    [RelayCommand]
    public async Task NavigateToArtistDetailAsync(object? parameter)
    {
        if (_isNavigating) return;
        try
        {
            _isNavigating = true;
            await _musicNavigationService.NavigateToArtistAsync(parameter);
        }
        finally
        {
            _isNavigating = false;
        }
    }

    /// <summary>
    ///     Clears the current queue and starts playing all songs by the selected artist.
    /// </summary>
    [RelayCommand]
    private async Task PlayArtistAsync(Guid artistId)
    {
        if (IsLoading || artistId == Guid.Empty) return;

        try
        {
            await _musicPlaybackService.PlayArtistAsync(artistId);
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Error playing artist {ArtistId}", artistId);
        }
    }

    /// <summary>
    ///     Changes the sort order and reloads the artist list.
    /// </summary>
    [RelayCommand]
    public async Task ChangeSortOrderAsync(string sortOrderString)
    {
        if (Enum.TryParse<ArtistSortOrder>(sortOrderString, out var newSortOrder)
            && newSortOrder != CurrentSortOrder)
        {
            CurrentSortOrder = newSortOrder;
            _ = _settingsService.SetSortOrderAsync(SortOrderHelper.ArtistsSortOrderKey, newSortOrder)
                .ContinueWith(t => _logger.LogError(t.Exception, "Failed to save artist sort order"),
                    TaskContinuationOptions.OnlyOnFaulted);
            await LoadArtistsCommand.ExecuteAsync(CancellationToken.None);
        }
    }

    private void UpdateSortOrderText()
    {
        CurrentSortOrderText = SortOrderHelper.GetDisplayName(CurrentSortOrder);
    }

    /// <summary>
    ///     Asynchronously loads artists with support for cancellation.
    /// </summary>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    [RelayCommand]
    public async Task LoadArtistsAsync(CancellationToken cancellationToken)
    {
        if (IsLoading) return;

        IsLoading = true;
        HasLoadError = false;
        _currentPage = 1;
        _isFullyLoaded = false;
        _artistLookup.Clear();
        Artists.Clear();
        
        try
        {
            if (!_hasSortOrderLoaded)
            {
                CurrentSortOrder = await _settingsService.GetSortOrderAsync<ArtistSortOrder>(SortOrderHelper.ArtistsSortOrderKey);
                _hasSortOrderLoaded = true;
            }

            await LoadNextPageAsync(cancellationToken);

            if (cancellationToken.IsCancellationRequested) return;

            // Optionally start background metadata fetching after initial load.
            if (await _settingsService.GetFetchOnlineMetadataEnabledAsync())
                await _libraryService.StartArtistMetadataBackgroundFetchAsync();

            // Continue loading subsequent pages in the background.
            if (!_isFullyLoaded)
            {
                IsLoadingMore = true;
                while (!_isFullyLoaded && !cancellationToken.IsCancellationRequested)
                {
                    _currentPage++;
                    await LoadNextPageAsync(cancellationToken);
                    await Task.Delay(250, cancellationToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Artist loading was canceled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An error occurred while loading artists");
            HasLoadError = true;
        }
        finally
        {
            IsLoading = false;
            IsLoadingMore = false;
        }
    }

    /// <summary>
    ///     Fetches a single page of artists from the library service.
    /// </summary>
    private async Task LoadNextPageAsync(CancellationToken cancellationToken)
    {
        PagedResult<Artist>? pagedResult;
        if (IsSearchActive)
            pagedResult = await _libraryService.SearchArtistsPagedAsync(SearchTerm, _currentPage, PageSize);
        else
            pagedResult = await _libraryService.GetAllArtistsPagedAsync(_currentPage, PageSize, CurrentSortOrder);

        if (cancellationToken.IsCancellationRequested) return;

        if (pagedResult?.Items?.Any() == true)
            foreach (var artist in pagedResult.Items)
            {
                var artistVm = new ArtistViewModelItem
                {
                    Id = artist.Id,
                    Name = artist.Name,
                    LocalImageCachePath = ImageUriHelper.GetUriWithCacheBuster(artist.LocalImageCachePath)
                };
                _artistLookup[artist.Id] = artistVm;
                Artists.Add(artistVm);
            }

        // Update the total items text.
        if (pagedResult != null)
        {
            var count = pagedResult.TotalCount;
            TotalItemsText = $"{count:N0} {(count == 1 ? "artist" : "artists")}";
        }

        if (pagedResult == null || Artists.Count >= pagedResult.TotalCount) _isFullyLoaded = true;
    }

    protected override async Task ExecuteSearchAsync(CancellationToken token)
    {
        await _dispatcherService.EnqueueAsync(async () =>
        {
            if (token.IsCancellationRequested) return;
            await LoadArtistsAsync(token);
        });
    }

    /// <summary>
    ///     Handles updates to artist metadata, such as new images.
    /// </summary>
    private void OnArtistMetadataUpdated(object? sender, ArtistMetadataUpdatedEventArgs e)
    {
        if (_artistLookup.TryGetValue(e.ArtistId, out var artistVm))
            // Ensure UI updates are performed on the main thread.
            _dispatcherService.TryEnqueue(() =>
            {
                // Guard against updates after the ViewModel has been disposed.
                if (_isDisposed) return;

                // Force refresh by setting to null first, then apply cache-buster
                artistVm.LocalImageCachePath = null;
                artistVm.LocalImageCachePath = ImageUriHelper.GetUriWithCacheBuster(e.NewLocalImageCachePath);
            });
    }

    /// <summary>
    ///     Subscribes to necessary service events.
    /// </summary>
    public void SubscribeToEvents()
    {
        _libraryService.ArtistMetadataUpdated += OnArtistMetadataUpdated;
    }

    /// <summary>
    ///     Unsubscribes from service events to prevent memory leaks.
    /// </summary>
    public void UnsubscribeFromEvents()
    {
        _libraryService.ArtistMetadataUpdated -= OnArtistMetadataUpdated;
    }

    /// <summary>
    ///     Cleans up search state when navigating away from the page.
    /// </summary>
    public override void Cleanup()
    {
        base.Cleanup();
        _logger.LogDebug("Cleaned up ArtistViewModel search resources");
    }

    [RelayCommand]
    public async Task UpdateArtistImageAsync(Tuple<Guid, string> artistData)
    {
        if (artistData == null) return;
        var (artistId, localPath) = artistData;
        await _libraryService.UpdateArtistImageAsync(artistId, localPath);
    }

    [RelayCommand]
    public async Task RemoveArtistImageAsync(Guid artistId)
    {
        await _libraryService.RemoveArtistImageAsync(artistId);
    }
}