// ArtistViewViewModel.cs
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
/// Represents a simplified album model for display within the artist view.
/// </summary>
public partial class ArtistAlbumViewModelItem : ObservableObject {
    public ArtistAlbumViewModelItem(Album album) {
        Id = album.Id;
        Name = album.Title;
        YearText = album.Year?.ToString() ?? string.Empty;
        CoverArtUri = album.Songs?
            .FirstOrDefault(s => !string.IsNullOrEmpty(s.AlbumArtUriFromTrack))
            ?.AlbumArtUriFromTrack;
    }

    public Guid Id { get; }
    public string Name { get; }
    public string? CoverArtUri { get; }
    public string YearText { get; }
}

/// <summary>
/// Provides data and logic for the ArtistViewPage, managing artist details, albums, and songs.
/// </summary>
public partial class ArtistViewViewModel : SongListViewModelBase {
    private Guid _artistId;
    private readonly DispatcherQueue _dispatcherQueue;

    public ArtistViewViewModel(ILibraryService libraryService, IMusicPlaybackService playbackService,
        INavigationService navigationService)
        : base(libraryService, playbackService, navigationService) {
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        CurrentSortOrder = SongSortOrder.AlbumAsc;
        UpdateSortOrderButtonText(CurrentSortOrder);
        Albums.CollectionChanged += (s, e) => OnPropertyChanged(nameof(HasAlbums));
        _libraryService.ArtistMetadataUpdated += OnArtistMetadataUpdated;
    }

    [ObservableProperty]
    public partial string ArtistName { get; set; } = "Artist";

    [ObservableProperty]
    public partial string ArtistBio { get; set; } = "Loading biography...";

    [ObservableProperty]
    public partial string? ArtistImageUri { get; set; }

    public ObservableCollection<ArtistAlbumViewModelItem> Albums { get; } = new();

    public bool HasAlbums => Albums.Any();

    [RelayCommand]
    public async Task LoadArtistDetailsAsync(Guid artistId) {
        if (IsOverallLoading) return;

        _artistId = artistId;

        try {
            var artist = await _libraryService.GetOrFetchArtistDetailsAsync(artistId);

            if (artist != null) {
                ArtistName = artist.Name;
                PageTitle = artist.Name;
                ArtistImageUri = artist.LocalImageCachePath;
                ArtistBio = string.IsNullOrWhiteSpace(artist.Biography) ? "No biography available for this artist." : artist.Biography;

                var albumVms = artist.Albums
                    .OrderByDescending(a => a.Year)
                    .ThenBy(a => a.Title)
                    .Select(album => new ArtistAlbumViewModelItem(album))
                    .ToList();

                Albums.Clear();
                foreach (var albumVm in albumVms) {
                    Albums.Add(albumVm);
                }

                await RefreshOrSortSongsCommand.ExecuteAsync(null);
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

    [RelayCommand]
    private void ViewAlbum(Guid albumId) {
        if (albumId != Guid.Empty) {
            _navigationService.Navigate(
                typeof(AlbumViewPage),
                new AlbumViewNavigationParameter { AlbumId = albumId });
        }
    }

    protected override async Task<IEnumerable<Song>> LoadSongsAsync() {
        if (_artistId == Guid.Empty) return Enumerable.Empty<Song>();
        return await _libraryService.GetSongsByArtistIdAsync(_artistId);
    }

    private void OnArtistMetadataUpdated(object? sender, ArtistMetadataUpdatedEventArgs e) {
        if (e.ArtistId == _artistId) {
            _dispatcherQueue.TryEnqueue(() => {
                ArtistImageUri = e.NewLocalImageCachePath;
            });
        }
    }

    public void Cleanup() {
        _libraryService.ArtistMetadataUpdated -= OnArtistMetadataUpdated;
    }
}