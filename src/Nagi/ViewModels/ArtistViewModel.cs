using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Nagi.Services.Abstractions;

namespace Nagi.ViewModels;

/// <summary>
/// Represents a single artist item in the main artist grid.
/// </summary>
public partial class ArtistViewModelItem : ObservableObject {
    [ObservableProperty]
    private Guid _id;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string? _localImageCachePath;
}

/// <summary>
/// Provides data and logic for the ArtistPage.
/// </summary>
public partial class ArtistViewModel : ObservableObject {
    private readonly ILibraryService _libraryService;
    private readonly DispatcherQueue _dispatcherQueue;

    // A dictionary for fast O(1) lookups of artists by their ID.
    private readonly Dictionary<Guid, ArtistViewModelItem> _artistLookup = new();

    [ObservableProperty]
    private bool _hasArtists;

    public ObservableCollection<ArtistViewModelItem> Artists { get; } = new();

    public ArtistViewModel(ILibraryService libraryService) {
        _libraryService = libraryService;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        _libraryService.ArtistMetadataUpdated += OnArtistMetadataUpdated;
    }

    [RelayCommand]
    private async Task LoadArtistsAsync() {
        var artistsFromDb = await _libraryService.GetAllArtistsAsync();

        // Build a temporary list first to avoid repeated 'Add' notifications on the UI thread.
        var newArtistVms = artistsFromDb.Select(artist => new ArtistViewModelItem {
            Id = artist.Id,
            Name = artist.Name,
            LocalImageCachePath = artist.LocalImageCachePath
        }).ToList();

        // Clear existing collections before populating.
        Artists.Clear();
        _artistLookup.Clear();

        // Add new items efficiently.
        foreach (var artistVm in newArtistVms) {
            Artists.Add(artistVm);
            _artistLookup.Add(artistVm.Id, artistVm);
        }

        HasArtists = Artists.Any();

        // After loading the artists, kick off the background process
        // to find and download any missing metadata (like images).
        await _libraryService.StartArtistMetadataBackgroundFetchAsync();
    }

    private void OnArtistMetadataUpdated(object? sender, ArtistMetadataUpdatedEventArgs e) {
        if (_artistLookup.TryGetValue(e.ArtistId, out var artistVm)) {
            // Ensure the UI update happens on the main thread.
            _dispatcherQueue.TryEnqueue(() => {
                artistVm.LocalImageCachePath = e.NewLocalImageCachePath;
            });
        }
    }

    public void Cleanup() {
        _libraryService.ArtistMetadataUpdated -= OnArtistMetadataUpdated;
    }
}