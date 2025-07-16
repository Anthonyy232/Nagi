using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Nagi.Models;
using Nagi.Navigation;
using Nagi.Pages;
using Nagi.Services.Abstractions;

namespace Nagi.ViewModels;

/// <summary>
/// Represents a single album in the artist's discography list.
/// </summary>
public partial class ArtistAlbumViewModelItem : ObservableObject {
    public Guid Id { get; }
    public string Name { get; }
    public string? CoverArtUri { get; }
    public string YearText { get; }

    public ArtistAlbumViewModelItem(Album album) {
        Id = album.Id;
        Name = album.Title;
        YearText = album.Year?.ToString() ?? string.Empty;
        CoverArtUri = album.CoverArtUri;
    }
}

/// <summary>
/// Provides data and commands for the artist details page, which displays an artist's
/// biography, albums, and a complete list of their songs.
/// </summary>
public partial class ArtistViewViewModel : SongListViewModelBase {
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly ISettingsService _settingsService;
    private readonly ILibraryScanner _libraryScanner;
    private Guid _artistId;

    public ArtistViewViewModel(
        ILibraryReader libraryReader,
        IPlaylistService playlistService,
        ILibraryScanner libraryScanner,
        IMusicPlaybackService playbackService,
        INavigationService navigationService,
        ISettingsService settingsService)
        : base(libraryReader, playlistService, playbackService, navigationService) {
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        _settingsService = settingsService;
        _libraryScanner = libraryScanner;
        CurrentSortOrder = SongSortOrder.AlbumAsc;
        UpdateSortOrderButtonText(CurrentSortOrder);
        Albums.CollectionChanged += (s, e) => OnPropertyChanged(nameof(HasAlbums));
    }

    [ObservableProperty]
    private string _artistName = "Artist";

    [ObservableProperty]
    private string _artistBio = "Loading biography...";

    [ObservableProperty]
    private string? _artistImageUri;

    /// <summary>
    /// A collection of albums by the artist.
    /// </summary>
    public ObservableCollection<ArtistAlbumViewModelItem> Albums { get; } = new();

    /// <summary>
    /// Gets a value indicating whether the artist has any albums in the library.
    /// </summary>
    public bool HasAlbums => Albums.Any();

    protected override bool IsPagingSupported => true;

    protected override Task<IEnumerable<Song>> LoadSongsAsync() {
        return Task.FromResult(Enumerable.Empty<Song>());
    }

    protected override async Task<PagedResult<Song>> LoadSongsPagedAsync(int pageNumber, int pageSize, SongSortOrder sortOrder) {
        if (_artistId == Guid.Empty) return new PagedResult<Song>();
        return await _libraryReader.GetSongsByArtistIdPagedAsync(_artistId, pageNumber, pageSize, sortOrder);
    }

    protected override async Task<List<Guid>> LoadAllSongIdsAsync(SongSortOrder sortOrder) {
        if (_artistId == Guid.Empty) return new List<Guid>();
        return await _libraryReader.GetAllSongIdsByArtistIdAsync(_artistId, sortOrder);
    }

    /// <summary>
    /// Loads the artist's details, including metadata, albums, and songs.
    /// This method also manages event subscriptions for metadata updates.
    /// </summary>
    /// <param name="artistId">The unique identifier of the artist to load.</param>
    [RelayCommand]
    public async Task LoadArtistDetailsAsync(Guid artistId) {
        if (IsOverallLoading) return;

        // Ensure event handlers are correctly wired for the current artist
        // to receive live updates, e.g., for newly downloaded cover art.
        _libraryScanner.ArtistMetadataUpdated -= OnArtistMetadataUpdated;
        _libraryScanner.ArtistMetadataUpdated += OnArtistMetadataUpdated;

        try {
            _artistId = artistId;
            var shouldFetchOnline = await _settingsService.GetFetchOnlineMetadataEnabledAsync();
            var artist = await _libraryScanner.GetArtistDetailsAsync(artistId, shouldFetchOnline);

            if (artist != null) {
                PopulateArtistDetails(artist);
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

    private void PopulateArtistDetails(Artist artist) {
        ArtistName = artist.Name;
        PageTitle = artist.Name;
        ArtistImageUri = artist.LocalImageCachePath;
        ArtistBio = string.IsNullOrWhiteSpace(artist.Biography)
            ? "No biography available for this artist."
            : artist.Biography;

        Albums.Clear();
        if (artist.Albums != null) {
            var albumVms = artist.Albums
                .OrderByDescending(a => a.Year)
                .ThenBy(a => a.Title)
                .Select(album => new ArtistAlbumViewModelItem(album));

            foreach (var albumVm in albumVms) {
                Albums.Add(albumVm);
            }
        }
    }

    private void HandleArtistNotFound(Guid artistId) {
        Debug.WriteLine($"[ArtistViewViewModel] INFO: Artist with ID '{artistId}' not found.");
        ArtistName = "Artist Not Found";
        PageTitle = "Not Found";
        ArtistBio = string.Empty;
        ArtistImageUri = null;
        Albums.Clear();
        Songs.Clear();
        TotalItemsText = "0 songs";
    }

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

    /// <summary>
    /// Navigates to the selected album's details page.
    /// </summary>
    /// <param name="albumId">The unique identifier of the album to view.</param>
    [RelayCommand(CanExecute = nameof(CanExecuteLoadCommands))]
    private void ViewAlbum(Guid albumId) {
        if (albumId == Guid.Empty) return;

        var album = Albums.FirstOrDefault(a => a.Id == albumId);
        if (album == null) return;

        _navigationService.Navigate(
            typeof(AlbumViewPage),
            new AlbumViewNavigationParameter { AlbumId = album.Id, AlbumTitle = album.Name, ArtistName = ArtistName });
    }

    /// <summary>
    /// Handles the <see cref="ILibraryScanner.ArtistMetadataUpdated"/> event to refresh
    /// UI elements like the artist's image when new metadata is available.
    /// </summary>
    private void OnArtistMetadataUpdated(object? sender, ArtistMetadataUpdatedEventArgs e) {
        if (e.ArtistId == _artistId) {
            _dispatcherQueue.TryEnqueue(() => { ArtistImageUri = e.NewLocalImageCachePath; });
        }
    }

    /// <summary>
    /// Cleans up resources by unsubscribing from library events to prevent memory leaks.
    /// </summary>
    public override void Cleanup() {
        base.Cleanup();
        _libraryScanner.ArtistMetadataUpdated -= OnArtistMetadataUpdated;
    }
}