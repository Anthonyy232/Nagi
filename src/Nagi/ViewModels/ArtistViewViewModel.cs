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
}

public partial class ArtistViewViewModel : SongListViewModelBase {
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly ISettingsService _settingsService;
    private Guid _artistId;

    public ArtistViewViewModel(ILibraryService libraryService, IMusicPlaybackService playbackService,
        INavigationService navigationService, ISettingsService settingsService)
        : base(libraryService, playbackService, navigationService) {
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        _settingsService = settingsService;
        CurrentSortOrder = SongSortOrder.AlbumAsc;
        UpdateSortOrderButtonText(CurrentSortOrder);
        Albums.CollectionChanged += (s, e) => OnPropertyChanged(nameof(HasAlbums));
        _libraryService.ArtistMetadataUpdated += OnArtistMetadataUpdated;
    }

    [ObservableProperty]
    private string _artistName = "Artist";

    [ObservableProperty]
    private string _artistBio = "Loading biography...";

    [ObservableProperty]
    private string? _artistImageUri;

    public ObservableCollection<ArtistAlbumViewModelItem> Albums { get; } = new();

    public bool HasAlbums => Albums.Any();

    protected override bool IsPagingSupported => true;

    protected override Task<IEnumerable<Song>> LoadSongsAsync() {
        return Task.FromResult(Enumerable.Empty<Song>());
    }

    protected override async Task<PagedResult<Song>> LoadSongsPagedAsync(int pageNumber, int pageSize, SongSortOrder sortOrder) {
        if (_artistId == Guid.Empty) {
            return new PagedResult<Song>();
        }
        return await _libraryService.GetSongsByArtistIdPagedAsync(_artistId, pageNumber, pageSize, sortOrder);
    }

    protected override async Task<List<Guid>> LoadAllSongIdsAsync(SongSortOrder sortOrder) {
        if (_artistId == Guid.Empty) {
            return new List<Guid>();
        }
        return await _libraryService.GetAllSongIdsByArtistIdAsync(_artistId, sortOrder);
    }

    [RelayCommand]
    public async Task LoadArtistDetailsAsync(Guid artistId) {
        if (IsOverallLoading) return;

        try {
            _artistId = artistId;
            var shouldFetchOnline = await _settingsService.GetFetchOnlineMetadataEnabledAsync();
            var artist = await _libraryService.GetArtistDetailsAsync(artistId, shouldFetchOnline);

            if (artist != null) {
                ArtistName = artist.Name;
                PageTitle = artist.Name;
                ArtistImageUri = artist.LocalImageCachePath;
                ArtistBio = string.IsNullOrWhiteSpace(artist.Biography)
                    ? "No biography available for this artist."
                    : artist.Biography;

                // Defensive check for null collection
                if (artist.Albums != null) {
                    var albumVms = artist.Albums
                        .OrderByDescending(a => a.Year)
                        .ThenBy(a => a.Title)
                        .Select(album => new ArtistAlbumViewModelItem(album))
                        .ToList();

                    Albums.Clear();
                    foreach (var albumVm in albumVms) Albums.Add(albumVm);
                }
                else {
                    Albums.Clear();
                }

                try {
                    await RefreshOrSortSongsCommand.ExecuteAsync(null);
                }
                catch (Exception ex) {
                    Debug.WriteLine($"[ERROR] Failed to load songs for artist '{artist.Name}'. {ex.Message}");
                    TotalItemsText = "Error loading songs";
                }
            }
            else {
                Debug.WriteLine($"Artist with ID '{artistId}' not found.");
                ArtistName = "Artist Not Found";
                PageTitle = "Not Found";
                ArtistBio = string.Empty;
                ArtistImageUri = null;
                Albums.Clear();
                Songs.Clear();
                TotalItemsText = "0 songs";
            }
        }
        catch (Exception ex) {
            Debug.WriteLine($"Error loading artist with ID '{artistId}': {ex.Message}");
            ArtistName = "Error Loading Artist";
            PageTitle = "Error";
            ArtistBio = "Could not load artist details.";
            ArtistImageUri = null;
            TotalItemsText = "Error";
            Albums.Clear();
            Songs.Clear();
        }
    }

    [RelayCommand(CanExecute = nameof(CanExecuteLoadCommands))]
    private void ViewAlbum(Guid albumId) {
        if (albumId == Guid.Empty) return;

        var album = Albums.FirstOrDefault(a => a.Id == albumId);
        if (album == null) return;

        _navigationService.Navigate(
            typeof(AlbumViewPage),
            new AlbumViewNavigationParameter { AlbumId = album.Id, AlbumTitle = album.Name, ArtistName = ArtistName });
    }

    private void OnArtistMetadataUpdated(object? sender, ArtistMetadataUpdatedEventArgs e) {
        if (e.ArtistId == _artistId) {
            _dispatcherQueue.TryEnqueue(() => { ArtistImageUri = e.NewLocalImageCachePath; });
        }
    }

    public override void Cleanup() {
        base.Cleanup();
        _libraryService.ArtistMetadataUpdated -= OnArtistMetadataUpdated;
    }
}