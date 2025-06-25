// Nagi/ViewModels/AlbumViewModel.cs

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nagi.Models;
using Nagi.Services;
using Nagi.Services.Abstractions;

namespace Nagi.ViewModels;

/// <summary>
///     Represents a display-optimized album item for the UI.
/// </summary>
public partial class AlbumViewModelItem : ObservableObject
{
    public AlbumViewModelItem(Album album)
    {
        Id = album.Id;
        Title = album.Title;
        ArtistName = album.Artist?.Name ?? "Unknown Artist";
        // Use the first available track's art as the album cover.
        CoverArtUri = album.Songs?.OrderBy(s => s.TrackNumber)
            .FirstOrDefault(s => !string.IsNullOrEmpty(s.AlbumArtUriFromTrack))
            ?.AlbumArtUriFromTrack;
    }

    public Guid Id { get; }

    [ObservableProperty] public partial string Title { get; set; }

    [ObservableProperty] public partial string ArtistName { get; set; }

    [ObservableProperty] public partial string? CoverArtUri { get; set; }
}

/// <summary>
///     Manages the state and logic for the page that displays a list of all albums.
/// </summary>
public partial class AlbumViewModel : ObservableObject
{
    private readonly ILibraryService _libraryService;

    public AlbumViewModel(ILibraryService libraryService)
    {
        _libraryService = libraryService;
        // Subscribes to collection changes to notify the UI to re-evaluate properties
        // like HasAlbums, which controls the visibility of the "No albums found" message.
        Albums.CollectionChanged += (s, e) => OnPropertyChanged(nameof(HasAlbums));
    }

    [ObservableProperty] public partial ObservableCollection<AlbumViewModelItem> Albums { get; set; } = new();

    [ObservableProperty] public partial bool IsLoading { get; set; }

    /// <summary>
    ///     Gets a value indicating whether the album collection contains any items.
    /// </summary>
    public bool HasAlbums => Albums.Any();

    /// <summary>
    ///     Asynchronously loads all albums from the library.
    ///     This method efficiently loads albums and songs separately and links them in memory
    ///     to determine cover art without causing N+1 query performance issues.
    /// </summary>
    [RelayCommand]
    public async Task LoadAlbumsAsync()
    {
        if (IsLoading) return;

        IsLoading = true;
        try
        {
            var albumsFromDb = await _libraryService.GetAllAlbumsAsync();
            var songsFromDb = await _libraryService.GetAllSongsAsync();

            var songsByAlbumId = songsFromDb
                .Where(s => s.AlbumId.HasValue)
                .GroupBy(s => s.AlbumId!.Value)
                .ToDictionary(g => g.Key, g => (ICollection<Song>)g.ToList());

            Albums.Clear();
            foreach (var album in albumsFromDb)
            {
                // Manually attach songs to the album object to find cover art.
                if (songsByAlbumId.TryGetValue(album.Id, out var songsForAlbum)) album.Songs = songsForAlbum;
                Albums.Add(new AlbumViewModelItem(album));
            }
        }
        catch (Exception ex)
        {
            // Log the error for debugging purposes.
            Debug.WriteLine($"[AlbumViewModel] Error loading albums: {ex.Message}");
        }
        finally
        {
            IsLoading = false;
        }
    }
}