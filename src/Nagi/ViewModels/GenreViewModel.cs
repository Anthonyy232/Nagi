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
/// A display-optimized representation of a genre for the user interface.
/// </summary>
public partial class GenreViewModelItem : ObservableObject {
    [ObservableProperty]
    private Guid _id;

    [ObservableProperty]
    private string _name = string.Empty;
}

/// <summary>
/// Manages the state and logic for the genre list page.
/// </summary>
public partial class GenreViewModel : ObservableObject {
    private readonly ILibraryService _libraryService;
    private readonly IMusicPlaybackService _musicPlaybackService;

    public GenreViewModel(ILibraryService libraryService, IMusicPlaybackService musicPlaybackService) {
        _libraryService = libraryService;
        _musicPlaybackService = musicPlaybackService;
        Genres.CollectionChanged += (s, e) => OnPropertyChanged(nameof(HasGenres));
    }

    [ObservableProperty]
    private ObservableCollection<GenreViewModelItem> _genres = new();

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _hasLoadError;

    /// <summary>
    /// Gets a value indicating whether there are any genres to display.
    /// </summary>
    public bool HasGenres => Genres.Any();

    /// <summary>
    /// Asynchronously loads all genres from the library.
    /// </summary>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    [RelayCommand]
    public async Task LoadGenresAsync(CancellationToken cancellationToken) {
        if (IsLoading) return;

        IsLoading = true;
        HasLoadError = false;

        try {
            var genreModels = await _libraryService.GetAllGenresAsync();
            if (cancellationToken.IsCancellationRequested) return;

            var sortedGenres = genreModels
                .OrderBy(g => g.Name)
                .Select(g => new GenreViewModelItem { Id = g.Id, Name = g.Name });

            // Efficiently replace the entire collection.
            Genres = new(sortedGenres);
        }
        catch (OperationCanceledException) {
            // This is an expected exception when navigation cancels the task. No action needed.
        }
        catch (Exception ex) {
            Debug.WriteLine($"[ERROR] Failed to load genres: {ex}");
            HasLoadError = true;
            Genres.Clear();
        }
        finally {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Clears the current queue and starts playing all songs in the selected genre.
    /// </summary>
    [RelayCommand]
    private async Task PlayGenreAsync(Guid genreId) {
        if (IsLoading || genreId == Guid.Empty) return;

        try {
            await _musicPlaybackService.PlayGenreAsync(genreId);
        }
        catch (Exception ex) {
            // This is a critical failure as it directly impacts core user functionality.
            Debug.WriteLine($"[CRITICAL] Failed to play genre {genreId}: {ex}");
        }
    }
}