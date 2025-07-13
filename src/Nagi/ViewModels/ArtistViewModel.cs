using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Nagi.Models;
using Nagi.Services.Abstractions;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Nagi.ViewModels;

/// <summary>
/// A display-optimized representation of an artist for the user interface.
/// </summary>
public partial class ArtistViewModelItem : ObservableObject {
    [ObservableProperty] private Guid _id;
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string? _localImageCachePath;
}

/// <summary>
/// Manages the state and logic for the artist list page, including data fetching and live updates.
/// </summary>
public partial class ArtistViewModel : ObservableObject {
    private readonly ILibraryService _libraryService;
    private readonly IMusicPlaybackService _musicPlaybackService;
    private readonly ISettingsService _settingsService;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly Dictionary<Guid, ArtistViewModelItem> _artistLookup = new();
    private int _currentPage = 1;
    private const int PageSize = 250;
    private bool _isFullyLoaded;

    public ArtistViewModel(ILibraryService libraryService, ISettingsService settingsService, IMusicPlaybackService musicPlaybackService) {
        _libraryService = libraryService;
        _settingsService = settingsService;
        _musicPlaybackService = musicPlaybackService;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        Artists.CollectionChanged += (s, e) => OnPropertyChanged(nameof(HasArtists));
    }

    [ObservableProperty]
    private ObservableCollection<ArtistViewModelItem> _artists = new();

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _isLoadingMore;

    [ObservableProperty]
    private bool _hasLoadError;

    public bool HasArtists => Artists.Any();

    /// <summary>
    /// Clears the current queue and starts playing all songs by the selected artist.
    /// </summary>
    [RelayCommand]
    private async Task PlayArtistAsync(Guid artistId) {
        if (IsLoading || artistId == Guid.Empty) return;

        try {
            await _musicPlaybackService.PlayArtistAsync(artistId);
        }
        catch (Exception ex) {
            Debug.WriteLine($"[ArtistViewModel] CRITICAL: Error playing artist {artistId}: {ex.Message}");
        }
    }

    /// <summary>
    /// Asynchronously loads artists with support for cancellation.
    /// </summary>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    [RelayCommand]
    public async Task LoadArtistsAsync(CancellationToken cancellationToken) {
        if (IsLoading) return;

        IsLoading = true;
        HasLoadError = false;
        _currentPage = 1;
        _isFullyLoaded = false;
        _artistLookup.Clear();
        Artists.Clear();

        try {
            await LoadNextPageAsync(cancellationToken);
            if (cancellationToken.IsCancellationRequested) return;

            // Optionally start background metadata fetching after initial load.
            if (await _settingsService.GetFetchOnlineMetadataEnabledAsync()) {
                await _libraryService.StartArtistMetadataBackgroundFetchAsync();
            }

            // Continue loading subsequent pages in the background.
            if (!_isFullyLoaded) {
                IsLoadingMore = true;
                while (!_isFullyLoaded && !cancellationToken.IsCancellationRequested) {
                    _currentPage++;
                    await LoadNextPageAsync(cancellationToken);
                    await Task.Delay(250, cancellationToken);
                }
            }
        }
        catch (OperationCanceledException) {
            Debug.WriteLine("[ArtistViewModel] Artist loading was canceled.");
        }
        catch (Exception ex) {
            Debug.WriteLine($"[ArtistViewModel] An error occurred while loading artists: {ex.Message}");
            HasLoadError = true;
        }
        finally {
            IsLoading = false;
            IsLoadingMore = false;
        }
    }

    /// <summary>
    /// Fetches a single page of artists from the library service.
    /// </summary>
    private async Task LoadNextPageAsync(CancellationToken cancellationToken) {
        var pagedResult = await _libraryService.GetAllArtistsPagedAsync(_currentPage, PageSize);

        if (cancellationToken.IsCancellationRequested) return;

        if (pagedResult?.Items?.Any() == true) {
            foreach (var artist in pagedResult.Items) {
                var artistVm = new ArtistViewModelItem {
                    Id = artist.Id,
                    Name = artist.Name,
                    LocalImageCachePath = artist.LocalImageCachePath
                };
                _artistLookup.Add(artist.Id, artistVm);
                Artists.Add(artistVm);
            }
        }

        if (pagedResult == null || Artists.Count >= pagedResult.TotalCount) {
            _isFullyLoaded = true;
        }
    }

    /// <summary>
    /// Handles updates to artist metadata, such as new images.
    /// </summary>
    private void OnArtistMetadataUpdated(object? sender, ArtistMetadataUpdatedEventArgs e) {
        if (_artistLookup.TryGetValue(e.ArtistId, out var artistVm)) {
            // Ensure UI updates are performed on the main thread.
            _dispatcherQueue.TryEnqueue(() => {
                artistVm.LocalImageCachePath = e.NewLocalImageCachePath;
            });
        }
    }

    /// <summary>
    /// Subscribes to necessary service events.
    /// </summary>
    public void SubscribeToEvents() => _libraryService.ArtistMetadataUpdated += OnArtistMetadataUpdated;

    /// <summary>
    /// Unsubscribes from service events to prevent memory leaks.
    /// </summary>
    public void UnsubscribeFromEvents() => _libraryService.ArtistMetadataUpdated -= OnArtistMetadataUpdated;
}