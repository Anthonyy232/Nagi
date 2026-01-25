using System;
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
///     A display-optimized representation of an album for the user interface.
/// </summary>
public partial class AlbumViewModelItem : ObservableObject
{
    public AlbumViewModelItem(Album album)
    {
        Id = album.Id;
        Title = album.Title;
        ArtistName = album.ArtistName;
        CoverArtUri = ImageUriHelper.GetUriWithCacheBuster(album.CoverArtUri);
    }

    public Guid Id { get; }
    [ObservableProperty] public partial string Title { get; set; }
    [ObservableProperty] public partial string ArtistName { get; set; }
    [ObservableProperty] public partial string? CoverArtUri { get; set; }

    public bool IsArtworkAvailable => !string.IsNullOrEmpty(CoverArtUri);

    partial void OnCoverArtUriChanged(string? value)
    {
        OnPropertyChanged(nameof(IsArtworkAvailable));
    }
}

/// <summary>
///     Manages the state and logic for the album list page, featuring gradual data fetching.
/// </summary>
public partial class AlbumViewModel : SearchableViewModelBase
{
    private const int PageSize = 250;
    private readonly NotifyCollectionChangedEventHandler _collectionChangedHandler;
    private readonly ILibraryService _libraryService;
    private readonly IMusicPlaybackService _musicPlaybackService;
    private readonly IUISettingsService _settingsService;
    private readonly INavigationService _navigationService;
    private readonly IMusicNavigationService _musicNavigationService;
    private int _currentPage = 1;
    private bool _isFullyLoaded;
    private bool _hasSortOrderLoaded;
    private bool _isNavigating;
    private CancellationTokenSource? _debouncer;

    public AlbumViewModel(ILibraryService libraryService, IMusicPlaybackService musicPlaybackService,
        INavigationService navigationService, IMusicNavigationService musicNavigationService,
        IUISettingsService settingsService, IDispatcherService dispatcherService, ILogger<AlbumViewModel> logger)
        : base(dispatcherService, logger)
    {
        _libraryService = libraryService;
        _musicPlaybackService = musicPlaybackService;
        _navigationService = navigationService;
        _musicNavigationService = musicNavigationService;
        _settingsService = settingsService;

        // Store the handler in a field so we can reliably unsubscribe from it later.
        _collectionChangedHandler = (sender, args) => OnPropertyChanged(nameof(HasAlbums));
        Albums.CollectionChanged += _collectionChangedHandler;
        
        // Subscribe to library changes to refresh when folders are added/removed
        _libraryService.LibraryContentChanged += OnLibraryContentChanged;
        
        UpdateSortOrderText();
    }
    
    private void OnLibraryContentChanged(object? sender, LibraryContentChangedEventArgs e)
    {
        // We don't need to refresh the album list just because a folder container was added (it has no songs/albums yet).
        if (e.ChangeType == LibraryChangeType.FolderAdded) return;
        
        // Debounce to prevent multiple refresh calls during rapid changes.
        var oldCts = Interlocked.Exchange(ref _debouncer, new CancellationTokenSource());
        try { oldCts?.Cancel(); } catch (ObjectDisposedException) { }
        
        var token = _debouncer.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(1000, token).ConfigureAwait(false);
                if (token.IsCancellationRequested) return;
                
                _logger.LogDebug("Library content changed ({ChangeType}). Refreshing album list.", e.ChangeType);
                await _dispatcherService.EnqueueAsync(() => LoadAlbumsCommand.ExecuteAsync(CancellationToken.None));
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error refreshing albums after library content change");
            }
        }, token);
    }

    [ObservableProperty] public partial ObservableCollection<AlbumViewModelItem> Albums { get; set; } = new();

    [ObservableProperty] public partial bool IsLoading { get; set; }

    [ObservableProperty] public partial bool IsLoadingMore { get; set; }

    [ObservableProperty] public partial bool HasLoadError { get; set; }

    [ObservableProperty] public partial AlbumSortOrder CurrentSortOrder { get; set; } = AlbumSortOrder.ArtistAsc;

    [ObservableProperty] public partial string CurrentSortOrderText { get; set; } = string.Empty;

    [ObservableProperty] public partial string TotalItemsText { get; set; } = "0 albums";

    partial void OnCurrentSortOrderChanged(AlbumSortOrder value) => UpdateSortOrderText();

    public bool HasAlbums => Albums.Any();

    private void UpdateSortOrderText()
    {
        CurrentSortOrderText = SortOrderHelper.GetDisplayName(CurrentSortOrder);
    }

    /// <summary>
    ///     Navigates to the detailed view for the selected album.
    /// </summary>
    [RelayCommand]
    public async Task NavigateToAlbumDetailAsync(object? parameter)
    {
        if (_isNavigating) return;
        try
        {
            _isNavigating = true;
            await _musicNavigationService.NavigateToAlbumAsync(parameter);
        }
        finally
        {
            _isNavigating = false;
        }
    }

    /// <summary>
    ///     Clears the current queue and starts playing all songs from the selected album.
    /// </summary>
    [RelayCommand]
    private async Task PlayAlbumAsync(Guid albumId)
    {
        if (IsLoading || albumId == Guid.Empty) return;

        try
        {
            await _musicPlaybackService.PlayAlbumAsync(albumId);
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Error playing album {AlbumId}", albumId);
        }
    }

    /// <summary>
    ///     Fetches a random album ID effectively instantly and starts playback.
    /// </summary>
    [RelayCommand]
    private async Task PlayRandomAlbumAsync()
    {
        if (IsLoading) return;

        try
        {
            var randomAlbumId = await _libraryService.GetRandomAlbumIdAsync();
            if (randomAlbumId.HasValue)
            {
                await _musicPlaybackService.PlayAlbumAsync(randomAlbumId.Value);
            }
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Error playing random album");
        }
    }

    [RelayCommand]
    public async Task GoToArtistAsync(object? parameter)
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
    ///     Changes the sort order and reloads the album list.
    /// </summary>
    [RelayCommand]
    public async Task ChangeSortOrderAsync(string sortOrderString)
    {
        if (Enum.TryParse<AlbumSortOrder>(sortOrderString, out var newSortOrder)
            && newSortOrder != CurrentSortOrder)
        {
            CurrentSortOrder = newSortOrder;
            _ = _settingsService.SetSortOrderAsync(SortOrderHelper.AlbumsSortOrderKey, newSortOrder)
                .ContinueWith(t => _logger.LogError(t.Exception, "Failed to save album sort order"),
                    TaskContinuationOptions.OnlyOnFaulted);
            await LoadAlbumsAsync(CancellationToken.None);
        }
    }

    /// <summary>
    ///     Asynchronously loads albums with support for cancellation.
    /// </summary>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    [RelayCommand]
    public async Task LoadAlbumsAsync(CancellationToken cancellationToken)
    {
        if (IsLoading) return;

        IsLoading = true;
        HasLoadError = false;
        _currentPage = 1;
        _isFullyLoaded = false;
        Albums.Clear();
        
        try
        {
            if (!_hasSortOrderLoaded)
            {
                CurrentSortOrder = await _settingsService.GetSortOrderAsync<AlbumSortOrder>(SortOrderHelper.AlbumsSortOrderKey);
                _hasSortOrderLoaded = true;
            }

            await LoadNextPageAsync(cancellationToken);

            if (cancellationToken.IsCancellationRequested) return;

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
            // Log that the operation was intentionally canceled.
            _logger.LogDebug("Album loading was canceled.");
        }
        catch (Exception ex)
        {
            // Log any unexpected errors during the loading process.
            _logger.LogError(ex, "An error occurred while loading albums");
            HasLoadError = true;
        }
        finally
        {
            IsLoading = false;
            IsLoadingMore = false;
        }
    }

    /// <summary>
    ///     Fetches a single page of albums from the library service.
    /// </summary>
    private async Task LoadNextPageAsync(CancellationToken cancellationToken)
    {
        PagedResult<Album>? pagedResult;
        if (IsSearchActive)
            pagedResult = await _libraryService.SearchAlbumsPagedAsync(SearchTerm, _currentPage, PageSize);
        else
            pagedResult = await _libraryService.GetAllAlbumsPagedAsync(_currentPage, PageSize, CurrentSortOrder);

        if (cancellationToken.IsCancellationRequested) return;

        if (pagedResult?.Items?.Any() == true)
            foreach (var album in pagedResult.Items)
                Albums.Add(new AlbumViewModelItem(album));

        // Update the total items text.
        if (pagedResult != null)
        {
            var count = pagedResult.TotalCount;
            TotalItemsText = $"{count:N0} {(count == 1 ? "album" : "albums")}";
        }

        // Determine if all albums have been loaded.
        if (pagedResult == null || Albums.Count >= pagedResult.TotalCount) _isFullyLoaded = true;
    }

    protected override async Task ExecuteSearchAsync(CancellationToken token)
    {
        await _dispatcherService.EnqueueAsync(async () =>
        {
            if (token.IsCancellationRequested) return;
            await LoadAlbumsAsync(token);
        });
    }
}
