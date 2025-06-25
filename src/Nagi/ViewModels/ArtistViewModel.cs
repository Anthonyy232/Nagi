using System;
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
///     Represents a display-ready artist item for the UI.
/// </summary>
public partial class ArtistViewModelItem : ObservableObject
{
    public ArtistViewModelItem(Artist artist)
    {
        Id = artist.Id;
        Name = artist.Name;
    }

    public Guid Id { get; }

    [ObservableProperty] public partial string Name { get; set; }
}

/// <summary>
///     Manages the state and logic for the artist page.
/// </summary>
public partial class ArtistViewModel : ObservableObject
{
    private readonly ILibraryService _libraryService;

    public ArtistViewModel(ILibraryService libraryService)
    {
        _libraryService = libraryService;

        // Notifies the UI to re-evaluate the HasArtists property
        // whenever the content of the Artists collection changes.
        Artists.CollectionChanged += (s, e) => OnPropertyChanged(nameof(HasArtists));
    }

    [ObservableProperty] public partial ObservableCollection<ArtistViewModelItem> Artists { get; set; } = new();

    [ObservableProperty] public partial bool IsLoading { get; set; }

    /// <summary>
    ///     Gets a value indicating whether the artist collection is not empty.
    /// </summary>
    public bool HasArtists => Artists.Any();

    /// <summary>
    ///     Asynchronously loads all artists from the library into the collection.
    /// </summary>
    [RelayCommand]
    private async Task LoadArtistsAsync()
    {
        if (IsLoading) return;

        IsLoading = true;
        try
        {
            var artistsFromDb = await _libraryService.GetAllArtistsAsync();

            Artists.Clear();
            foreach (var artist in artistsFromDb) Artists.Add(new ArtistViewModelItem(artist));
        }
        catch (Exception ex)
        {
            // In a production app, a more robust logging mechanism should be used.
            Debug.WriteLine($"[ArtistViewModel] Error loading artists: {ex.Message}");
        }
        finally
        {
            IsLoading = false;
        }
    }
}