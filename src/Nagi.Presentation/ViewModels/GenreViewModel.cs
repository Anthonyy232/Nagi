using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Nagi.Core.Helpers;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;
using Nagi.Presentation.Helpers;
using Nagi.Presentation.Navigation;
using Nagi.Presentation.Services.Abstractions;

namespace Nagi.Presentation.ViewModels;

public partial class GenreViewModel : SearchableViewModelBase, IDisposable
{
    private readonly ILibraryReader _libraryReader;
    private readonly ILibraryScanner _libraryScanner;
    private readonly IMusicPlaybackService _musicPlaybackService;
    private readonly INavigationService _navigationService;
    private readonly IUISettingsService _settingsService;
    private bool _hasSortOrderLoaded;
    private List<GenreSummary> _allGenres = new();
    private readonly Debouncer _debouncer = new(TimeSpan.FromSeconds(1));
    private readonly SemaphoreSlim _loadLock = new(1, 1);

    public GenreViewModel(ILibraryReader libraryReader, ILibraryScanner libraryScanner, IMusicPlaybackService musicPlaybackService,
        INavigationService navigationService, IUISettingsService settingsService, IDispatcherService dispatcherService, ILogger<GenreViewModel> logger)
        : base(dispatcherService, logger)
    {
        _libraryReader = libraryReader;
        _libraryScanner = libraryScanner;
        _musicPlaybackService = musicPlaybackService;
        _navigationService = navigationService;
        _settingsService = settingsService;

        Genres.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasGenres));

        _libraryScanner.LibraryContentChanged += OnLibraryContentChanged;

    }

    private void OnLibraryContentChanged(object? sender, LibraryContentChangedEventArgs e)
    {
        // We don't need to refresh the genre list just because a folder container was added (it has no songs yet).
        if (e.ChangeType == LibraryChangeType.FolderAdded) return;

        _debouncer.Trigger(async token =>
        {
            try
            {
                _logger.LogDebug("Library content changed ({ChangeType}). Refreshing genre list.", e.ChangeType);
                await _dispatcherService.EnqueueAsync(() => token.IsCancellationRequested
                    ? Task.CompletedTask : LoadGenresAsync(token));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Error refreshing genres after library content change");
            }
        });
    }

    public override void ResetState()
    {
        base.ResetState();
        _debouncer.Cancel();
        LoadGenresCommand.Cancel();
    }

    public void Dispose()
    {
        _libraryScanner.LibraryContentChanged -= OnLibraryContentChanged;
        ResetState();
    }

    public ObservableRangeCollection<GenreSummary> Genres { get; } = new();

    [ObservableProperty] public partial bool IsLoading { get; set; }

    [ObservableProperty] public partial bool HasLoadError { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentSortOrderText))]
    public partial GenreSortOrder CurrentSortOrder { get; set; } = GenreSortOrder.NameAsc;

    public string CurrentSortOrderText => SortOrderHelper.GetDisplayName(CurrentSortOrder);

    public bool HasGenres => Genres.Any();

    public void NavigateToGenreDetail(GenreSummary? genre)
    {
        if (genre is null) return;
        var navParam = new GenreViewNavigationParameter
        {
            GenreId = genre.Id,
            GenreName = genre.Name
        };
        _navigationService.NavigateTo(navParam);
    }

    [RelayCommand]
    public async Task LoadGenresAsync(CancellationToken cancellationToken)
    {
        try { await _loadLock.WaitAsync(cancellationToken); }
        catch (OperationCanceledException) { return; }

        IsLoading = true;
        HasLoadError = false;

        try
        {
            if (!_hasSortOrderLoaded)
            {
                CurrentSortOrder = await _settingsService.GetSortOrderAsync<GenreSortOrder>(SortOrderHelper.GenresSortOrderKey);
                _hasSortOrderLoaded = true;
            }
            var genreModels = await Task.Run(() => _libraryReader.GetGenreSummariesAsync(cancellationToken), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            _allGenres = genreModels.ToList();

            ApplyFilter();
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Genre loading was canceled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load genres");
            HasLoadError = true;
            _allGenres.Clear();
            Genres.Clear();
        }
        finally
        {
            IsLoading = false;
            _loadLock.Release();
        }
    }

    protected override Task ExecuteSearchAsync(CancellationToken token)
    {
        return _dispatcherService.EnqueueAsync(() =>
        {
            if (!token.IsCancellationRequested) ApplyFilter();
            return Task.CompletedTask;
        });
    }

    private void ApplyFilter()
    {
        IEnumerable<GenreSummary> filtered = _allGenres;
        if (IsSearchActive)
            filtered = _allGenres.Where(g =>
                g.Name.Contains(SearchTerm, StringComparison.OrdinalIgnoreCase));

        var sorted = CurrentSortOrder switch
        {
            GenreSortOrder.NameDesc => filtered.OrderByDescending(g => g.Name, StringComparer.OrdinalIgnoreCase).ThenBy(g => g.Id),
            GenreSortOrder.SongCountDesc => filtered.OrderByDescending(g => g.SongCount).ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ThenBy(g => g.Id),
            GenreSortOrder.SongCountAsc => filtered.OrderBy(g => g.SongCount).ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ThenBy(g => g.Id),
            _ => filtered.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ThenBy(g => g.Id)
        };

        Genres.ReplaceRange(sorted);
    }

    [RelayCommand]
    public async Task ChangeSortOrderAsync(string sortOrderString)
    {
        if (Enum.TryParse<GenreSortOrder>(sortOrderString, out var newSortOrder)
            && newSortOrder != CurrentSortOrder)
        {
            CurrentSortOrder = newSortOrder;
            await _settingsService.SetSortOrderAsync(SortOrderHelper.GenresSortOrderKey, newSortOrder);
            ApplyFilter();
        }
    }

    [RelayCommand]
    private async Task PlayGenreAsync(Guid genreId)
    {
        if (IsLoading || genreId == Guid.Empty) return;

        try
        {
            await _musicPlaybackService.PlayGenreAsync(genreId);
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Failed to play genre {GenreId}", genreId);
        }
    }

    [RelayCommand]
    private async Task PlayRandomGenreAsync()
    {
        if (IsLoading) return;

        try
        {
            var randomGenreId = await _libraryReader.GetRandomGenreIdAsync();
            if (randomGenreId.HasValue)
            {
                await _musicPlaybackService.PlayGenreAsync(randomGenreId.Value);
            }
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Error playing random genre");
        }
    }
}
