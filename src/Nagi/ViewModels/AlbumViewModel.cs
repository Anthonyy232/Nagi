using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nagi.Models;
using Nagi.Services.Abstractions;
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Nagi.ViewModels;

/// <summary>
/// A display-optimized representation of an album for the user interface.
/// </summary>
public partial class AlbumViewModelItem : ObservableObject {
    public AlbumViewModelItem(Album album) {
        Id = album.Id;
        Title = album.Title;
        ArtistName = album.Artist?.Name ?? "Unknown Artist";
        CoverArtUri = album.CoverArtUri;
    }

    public Guid Id { get; }
    [ObservableProperty] public partial string Title { get; set; }
    [ObservableProperty] public partial string ArtistName { get; set; }
    [ObservableProperty] public partial string? CoverArtUri { get; set; }

    public bool IsArtworkAvailable => !string.IsNullOrEmpty(CoverArtUri);

    partial void OnCoverArtUriChanged(string? value) {
        OnPropertyChanged(nameof(IsArtworkAvailable));
    }
}

/// <summary>
/// Manages the state and logic for the album list page, featuring gradual data fetching.
/// </summary>
public partial class AlbumViewModel : ObservableObject {
    private readonly ILibraryService _libraryService;
    private readonly IMusicPlaybackService _musicPlaybackService;
    private int _currentPage = 1;
    private const int PageSize = 250;
    private bool _isFullyLoaded;

    public AlbumViewModel(ILibraryService libraryService, IMusicPlaybackService musicPlaybackService) {
        _libraryService = libraryService;
        _musicPlaybackService = musicPlaybackService;
        Albums.CollectionChanged += (sender, args) => OnPropertyChanged(nameof(HasAlbums));
    }

    [ObservableProperty]
    public partial ObservableCollection<AlbumViewModelItem> Albums { get; set; } = new();

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial bool IsLoadingMore { get; set; }

    [ObservableProperty]
    public partial bool HasLoadError { get; set; }

    public bool HasAlbums => Albums.Any();

    /// <summary>
    /// Clears the current queue and starts playing all songs from the selected album.
    /// </summary>
    [RelayCommand]
    private async Task PlayAlbumAsync(Guid albumId) {
        if (IsLoading || albumId == Guid.Empty) return;

        try {
            await _musicPlaybackService.PlayAlbumAsync(albumId);
        }
        catch (Exception ex) {
            Debug.WriteLine($"[AlbumViewModel] CRITICAL: Error playing album {albumId}: {ex.Message}");
        }
    }

    /// <summary>
    /// Asynchronously loads albums with support for cancellation.
    /// </summary>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    [RelayCommand]
    public async Task LoadAlbumsAsync(CancellationToken cancellationToken) {
        if (IsLoading) return;

        IsLoading = true;
        HasLoadError = false;
        _currentPage = 1;
        _isFullyLoaded = false;
        Albums.Clear();

        try {
            await LoadNextPageAsync(cancellationToken);
            if (cancellationToken.IsCancellationRequested) return;

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
            // Log that the operation was intentionally canceled.
            Debug.WriteLine("[AlbumViewModel] Album loading was canceled.");
        }
        catch (Exception ex) {
            // Log any unexpected errors during the loading process.
            Debug.WriteLine($"[AlbumViewModel] An error occurred while loading albums: {ex.Message}");
            HasLoadError = true;
        }
        finally {
            IsLoading = false;
            IsLoadingMore = false;
        }
    }

    /// <summary>
    /// Fetches a single page of albums from the library service.
    /// </summary>
    private async Task LoadNextPageAsync(CancellationToken cancellationToken) {
        var pagedResult = await _libraryService.GetAllAlbumsPagedAsync(_currentPage, PageSize);

        if (cancellationToken.IsCancellationRequested) return;

        if (pagedResult?.Items?.Any() == true) {
            foreach (var album in pagedResult.Items) {
                Albums.Add(new AlbumViewModelItem(album));
            }
        }

        // Determine if all albums have been loaded.
        if (pagedResult == null || Albums.Count >= pagedResult.TotalCount) {
            _isFullyLoaded = true;
        }
    }
}