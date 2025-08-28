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

    partial void OnLocalImageCachePathChanged(string? value)
    {
        OnPropertyChanged(nameof(IsArtworkAvailable));
    }
}

/// <summary>
///     Manages the state and logic for the artist list page, including data fetching and live updates.
/// </summary>
public partial class ArtistViewModel : ObservableObject, IDisposable
{
    private const int PageSize = 250;
    private readonly Dictionary<Guid, ArtistViewModelItem> _artistLookup = new();
    private readonly NotifyCollectionChangedEventHandler _collectionChangedHandler;
    private readonly IDispatcherService _dispatcherService;
    private readonly ILibraryService _libraryService;
    private readonly ILogger<ArtistViewModel> _logger;
    private readonly IMusicPlaybackService _musicPlaybackService;
    private readonly INavigationService _navigationService;
    private readonly ISettingsService _settingsService;
    private int _currentPage = 1;
    private bool _isDisposed;
    private bool _isFullyLoaded;

    public ArtistViewModel(
        ILibraryService libraryService,
        ISettingsService settingsService,
        IMusicPlaybackService musicPlaybackService,
        IDispatcherService dispatcherService,
        INavigationService navigationService,
        ILogger<ArtistViewModel> logger)
    {
        _libraryService = libraryService;
        _settingsService = settingsService;
        _musicPlaybackService = musicPlaybackService;
        _dispatcherService = dispatcherService;
        _navigationService = navigationService;
        _logger = logger;

        // Store the handler in a field so we can reliably unsubscribe from it later.
        _collectionChangedHandler = (s, e) => OnPropertyChanged(nameof(HasArtists));
        Artists.CollectionChanged += _collectionChangedHandler;
    }

    [ObservableProperty] public partial ObservableCollection<ArtistViewModelItem> Artists { get; set; } = new();

    [ObservableProperty] public partial bool IsLoading { get; set; }

    [ObservableProperty] public partial bool IsLoadingMore { get; set; }

    [ObservableProperty] public partial bool HasLoadError { get; set; }

    public bool HasArtists => Artists.Any();

    /// <summary>
    ///     Cleans up resources by unsubscribing from event handlers.
    /// </summary>
    public void Dispose()
    {
        if (_isDisposed) return;

        if (Artists != null) Artists.CollectionChanged -= _collectionChangedHandler;
        _libraryService.ArtistMetadataUpdated -= OnArtistMetadataUpdated;

        _isDisposed = true;
        GC.SuppressFinalize(this);
    }

    /// <summary>
    ///     Navigates to the detailed view for the selected artist.
    /// </summary>
    [RelayCommand]
    public void NavigateToArtistDetail(ArtistViewModelItem? artist)
    {
        if (artist is null) return;

        var navParam = new ArtistViewNavigationParameter
        {
            ArtistId = artist.Id,
            ArtistName = artist.Name
        };
        _navigationService.Navigate(typeof(ArtistViewPage), navParam);
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
            _logger.LogInformation("Artist loading was canceled.");
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
        var pagedResult = await _libraryService.GetAllArtistsPagedAsync(_currentPage, PageSize);

        if (cancellationToken.IsCancellationRequested) return;

        if (pagedResult?.Items?.Any() == true)
            foreach (var artist in pagedResult.Items)
            {
                var artistVm = new ArtistViewModelItem
                {
                    Id = artist.Id,
                    Name = artist.Name,
                    LocalImageCachePath = artist.LocalImageCachePath
                };
                _artistLookup.Add(artist.Id, artistVm);
                Artists.Add(artistVm);
            }

        if (pagedResult == null || Artists.Count >= pagedResult.TotalCount) _isFullyLoaded = true;
    }

    /// <summary>
    ///     Handles updates to artist metadata, such as new images.
    /// </summary>
    private void OnArtistMetadataUpdated(object? sender, ArtistMetadataUpdatedEventArgs e)
    {
        if (_artistLookup.TryGetValue(e.ArtistId, out var artistVm))
            // Ensure UI updates are performed on the main thread.
            _dispatcherService.TryEnqueue(() => { artistVm.LocalImageCachePath = e.NewLocalImageCachePath; });
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
}