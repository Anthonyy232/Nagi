using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;
using Nagi.WinUI.Navigation;
using Nagi.WinUI.Pages;
using Nagi.WinUI.Services.Abstractions;

namespace Nagi.WinUI.ViewModels;

/// <summary>
///     Represents a single album in the artist's discography list.
/// </summary>
public partial class ArtistAlbumViewModelItem : ObservableObject {
    public ArtistAlbumViewModelItem(Album album) {
        Id = album.Id;
        Name = album.Title;
        YearText = album.Year?.ToString() ?? string.Empty;
        CoverArtUri = album.CoverArtUri;
    }

    public Guid Id { get; }
    public string Name { get; }
    public string? CoverArtUri { get; }
    public string YearText { get; }
    public bool IsArtworkAvailable => !string.IsNullOrEmpty(CoverArtUri);
}

/// <summary>
///     Provides data and commands for the artist details page, which displays an artist's
///     biography, albums, and a complete list of their songs.
/// </summary>
public partial class ArtistViewViewModel : SongListViewModelBase {
    private const int SearchDebounceDelay = 400;
    private readonly ILibraryScanner _libraryScanner;
    private readonly ISettingsService _settingsService;
    private Guid _artistId;
    private CancellationTokenSource? _debounceCts;

    public ArtistViewViewModel(
        ILibraryReader libraryReader,
        IPlaylistService playlistService,
        ILibraryScanner libraryScanner,
        IMusicPlaybackService playbackService,
        INavigationService navigationService,
        ISettingsService settingsService,
        IDispatcherService dispatcherService,
        IUIService uiService)
        : base(libraryReader, playlistService, playbackService, navigationService, dispatcherService, uiService) {
        _settingsService = settingsService;
        _libraryScanner = libraryScanner;

        // Initialize properties with default values
        ArtistName = "Artist";
        ArtistBio = "Loading biography...";
        SearchTerm = string.Empty;

        CurrentSortOrder = SongSortOrder.AlbumAsc;
        UpdateSortOrderButtonText(CurrentSortOrder);
        Albums.CollectionChanged += (s, e) => OnPropertyChanged(nameof(HasAlbums));
    }

    [ObservableProperty]
    public partial string ArtistName { get; set; }

    [ObservableProperty]
    public partial string ArtistBio { get; set; }

    [ObservableProperty]
    public partial string? ArtistImageUri { get; set; }

    [ObservableProperty]
    public partial string SearchTerm { get; set; }

    partial void OnSearchTermChanged(string value) {
        TriggerDebouncedSearch();
    }

    private bool IsSearchActive => !string.IsNullOrWhiteSpace(SearchTerm);

    public ObservableCollection<ArtistAlbumViewModelItem> Albums { get; } = new();
    public bool HasAlbums => Albums.Any();
    protected override bool IsPagingSupported => true;

    // Paging is handled by the specialized methods below.
    protected override Task<IEnumerable<Song>> LoadSongsAsync() {
        return Task.FromResult(Enumerable.Empty<Song>());
    }

    protected override async Task<PagedResult<Song>> LoadSongsPagedAsync(int pageNumber, int pageSize,
        SongSortOrder sortOrder) {
        if (_artistId == Guid.Empty) return new PagedResult<Song>();

        if (IsSearchActive)
            return await _libraryReader.SearchSongsInArtistPagedAsync(_artistId, SearchTerm, pageNumber, pageSize);

        return await _libraryReader.GetSongsByArtistIdPagedAsync(_artistId, pageNumber, pageSize, sortOrder);
    }

    protected override async Task<List<Guid>> LoadAllSongIdsAsync(SongSortOrder sortOrder) {
        if (_artistId == Guid.Empty) return new List<Guid>();

        if (IsSearchActive)
            return await _libraryReader.SearchAllSongIdsInArtistAsync(_artistId, SearchTerm, sortOrder);

        return await _libraryReader.GetAllSongIdsByArtistIdAsync(_artistId, sortOrder);
    }

    [RelayCommand]
    public async Task LoadArtistDetailsAsync(Guid artistId) {
        if (IsOverallLoading) return;
        Debug.WriteLine($"[ArtistViewViewModel] INFO: Loading details for artist ID '{artistId}'.");

        // Ensure we don't attach multiple handlers if this method is called again.
        _libraryScanner.ArtistMetadataUpdated -= OnArtistMetadataUpdated;
        _libraryScanner.ArtistMetadataUpdated += OnArtistMetadataUpdated;

        try {
            _artistId = artistId;
            var shouldFetchOnline = await _settingsService.GetFetchOnlineMetadataEnabledAsync();
            var artist = await _libraryScanner.GetArtistDetailsAsync(artistId, shouldFetchOnline);

            if (artist != null) {
                PopulateArtistDetails(artist);
                // After populating artist details, load the associated song list.
                await RefreshOrSortSongsCommand.ExecuteAsync(null);
            }
            else {
                HandleArtistNotFound(artistId);
            }
        }
        catch (Exception ex) {
            HandleLoadError(artistId, ex);
        }
    }

    /// <summary>
    ///     Populates the ViewModel's properties from the Artist model.
    /// </summary>
    private void PopulateArtistDetails(Artist artist) {
        ArtistName = artist.Name;
        PageTitle = artist.Name;
        ArtistImageUri = artist.LocalImageCachePath;
        ArtistBio = string.IsNullOrWhiteSpace(artist.Biography)
            ? "No biography available for this artist."
            : artist.Biography;

        Albums.Clear();
        if (artist.Albums != null) {
            // Order albums by year descending, then alphabetically for a standard discography view.
            var albumVms = artist.Albums
                .OrderByDescending(a => a.Year)
                .ThenBy(a => a.Title)
                .Select(album => new ArtistAlbumViewModelItem(album));

            foreach (var albumVm in albumVms) Albums.Add(albumVm);
        }

        Debug.WriteLine($"[ArtistViewViewModel] INFO: Populated details for artist '{artist.Name}'.");
    }

    /// <summary>
    ///     Handles the UI state when an artist cannot be found in the library.
    /// </summary>
    private void HandleArtistNotFound(Guid artistId) {
        Debug.WriteLine($"[ArtistViewViewModel] WARN: Artist with ID '{artistId}' not found.");
        ArtistName = "Artist Not Found";
        PageTitle = "Not Found";
        ArtistBio = string.Empty;
        ArtistImageUri = null;
        Albums.Clear();
        Songs.Clear();
        TotalItemsText = "0 songs";
    }

    /// <summary>
    ///     Handles the UI state when an exception occurs during loading.
    /// </summary>
    private void HandleLoadError(Guid artistId, Exception ex) {
        Debug.WriteLine($"[ArtistViewViewModel] ERROR: Failed to load artist with ID '{artistId}'. {ex.Message}");
        ArtistName = "Error Loading Artist";
        PageTitle = "Error";
        ArtistBio = "Could not load artist details.";
        ArtistImageUri = null;
        TotalItemsText = "Error";
        Albums.Clear();
        Songs.Clear();
    }

    [RelayCommand(CanExecute = nameof(CanExecuteLoadCommands))]
    private void ViewAlbum(Guid albumId) {
        if (albumId == Guid.Empty) return;

        var album = Albums.FirstOrDefault(a => a.Id == albumId);
        if (album == null) {
            Debug.WriteLine(
                $"[ArtistViewViewModel] WARN: Attempted to navigate to non-existent album ID '{albumId}'.");
            return;
        }

        Debug.WriteLine($"[ArtistViewViewModel] INFO: Navigating to album '{album.Name}'.");
        _navigationService.Navigate(
            typeof(AlbumViewPage),
            new AlbumViewNavigationParameter { AlbumId = album.Id, AlbumTitle = album.Name, ArtistName = ArtistName });
    }

    /// <summary>
    ///     Handles real-time updates to artist metadata, such as a downloaded artist image.
    /// </summary>
    private void OnArtistMetadataUpdated(object? sender, ArtistMetadataUpdatedEventArgs e) {
        // Update the image only if it's for the currently displayed artist.
        if (e.ArtistId == _artistId) {
            Debug.WriteLine(
                $"[ArtistViewViewModel] INFO: Received metadata update for artist ID '{_artistId}'. Updating image URI.");
            // Must be run on the UI thread to update the bound property.
            _dispatcherService.TryEnqueue(() => { ArtistImageUri = e.NewLocalImageCachePath; });
        }
    }

    /// <summary>
    /// Executes an immediate search or refresh, cancelling any pending debounced search.
    /// </summary>
    [RelayCommand]
    private async Task SearchAsync() {
        _debounceCts?.Cancel();
        await RefreshOrSortSongsCommand.ExecuteAsync(null);
    }

    private void TriggerDebouncedSearch() {
        try {
            _debounceCts?.Cancel();
        }
        catch (ObjectDisposedException) {
            // Ignore exception if the CancellationTokenSource has already been disposed.
        }

        _debounceCts = new CancellationTokenSource();
        var token = _debounceCts.Token;

        Task.Run(async () => {
            try {
                await Task.Delay(SearchDebounceDelay, token);

                if (token.IsCancellationRequested) return;

                await _dispatcherService.EnqueueAsync(async () => {
                    // Re-check the cancellation token after dispatching to prevent a race condition.
                    if (token.IsCancellationRequested) return;
                    await RefreshOrSortSongsCommand.ExecuteAsync(null);
                });
            }
            catch (TaskCanceledException) {
                Debug.WriteLine("[ArtistViewViewModel] Debounced search cancelled.");
            }
            catch (Exception ex) {
                Debug.WriteLine($"[ArtistViewViewModel] ERROR: Debounced search failed. {ex.Message}");
            }
        }, token);
    }

    /// <summary>
    ///     Unsubscribes from events to prevent memory leaks.
    /// </summary>
    public override void Cleanup() {
        _debounceCts?.Cancel();
        _debounceCts?.Dispose();
        SearchTerm = string.Empty;

        base.Cleanup();
        _libraryScanner.ArtistMetadataUpdated -= OnArtistMetadataUpdated;
        // Also unsubscribe from the collection changed event for completeness.
        Albums.CollectionChanged -= (s, e) => OnPropertyChanged(nameof(HasAlbums));
        Debug.WriteLine($"[ArtistViewViewModel] INFO: Cleaned up for artist ID '{_artistId}'.");
    }
}