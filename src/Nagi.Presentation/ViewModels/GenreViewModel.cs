using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;
using Nagi.Presentation.Helpers;
using Nagi.Presentation.Services.Abstractions;
using Nagi.Presentation.Services;
using Nagi.Core.Settings;

namespace Nagi.Presentation.ViewModels;

public partial class GenreViewModel : SearchableViewModelBase, IDisposable
{
    private readonly ILibraryReader _libraryReader;
    private readonly ILibraryNotifier _libraryNotifier;
    private readonly IMusicPlaybackService _musicPlaybackService;
    private readonly IMusicNavigationService _navigationService;
    private readonly ISettingsService _settingsService;
    private readonly IUIService _uiService;
    private List<GenreSummary> _allGenres = new();
    private readonly Debouncer _debouncer = new(TimeSpan.FromSeconds(1));
    private readonly SemaphoreSlim _loadLock = new(1, 1);

    public GenreViewModel(ILibraryReader libraryReader, ILibraryNotifier libraryNotifier, IMusicPlaybackService musicPlaybackService,
        IMusicNavigationService navigationService, ISettingsService settingsService, IDispatcherService dispatcherService, IUIService uiService,
        ILogger<GenreViewModel> logger)
        : base(dispatcherService, logger)
    {
        _libraryReader = libraryReader;
        _libraryNotifier = libraryNotifier;
        _musicPlaybackService = musicPlaybackService;
        _navigationService = navigationService;
        _settingsService = settingsService;
        _uiService = uiService;
        Sort = new SortOrderSelection<GenreSortOrder>(SortOrders, AppSettings.GenresSortOrder, settingsService, () =>
        {
            ApplyFilter();
            return Task.CompletedTask;
        }, logger);

        Genres.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasGenres));

        _libraryNotifier.Changed += OnLibraryChanged;

    }

    private void OnLibraryChanged(object? sender, LibraryChange change)
    {
        // Song counts per genre change only when songs do.
        if (change.Kind is LibraryChangeKind.RootAdded or LibraryChangeKind.FavoritesChanged or LibraryChangeKind.ListenHistoryCleared) return;

        _debouncer.Trigger(async token =>
        {
            try
            {
                _logger.LogDebug("Library changed ({ChangeKind}). Refreshing genre list.", change.Kind);
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
        _libraryNotifier.Changed -= OnLibraryChanged;
        ResetState();
    }

    public ObservableRangeCollection<GenreSummary> Genres { get; } = new();

    [ObservableProperty] public partial bool IsLoading { get; set; }

    [ObservableProperty] public partial bool HasLoadError { get; set; }

    private static readonly GenreSortOrder[] SortOrders =
        [GenreSortOrder.NameAsc, GenreSortOrder.NameDesc, GenreSortOrder.SongCountAsc, GenreSortOrder.SongCountDesc];

    public SortOrderSelection<GenreSortOrder> Sort { get; }

    public bool HasGenres => Genres.Any();

    public void OpenGenre(GenreSummary genre) => _navigationService.OpenGenre(genre.Id, genre.Name);

    [RelayCommand]
    public async Task LoadGenresAsync(CancellationToken cancellationToken)
    {
        try { await _loadLock.WaitAsync(cancellationToken); }
        catch (OperationCanceledException) { return; }

        IsLoading = true;
        HasLoadError = false;

        try
        {
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

        var sorted = Sort.Current switch
        {
            GenreSortOrder.NameDesc => filtered.OrderByDescending(g => g.Name, StringComparer.OrdinalIgnoreCase).ThenBy(g => g.Id),
            GenreSortOrder.SongCountDesc => filtered.OrderByDescending(g => g.SongCount).ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ThenBy(g => g.Id),
            GenreSortOrder.SongCountAsc => filtered.OrderBy(g => g.SongCount).ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ThenBy(g => g.Id),
            _ => filtered.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ThenBy(g => g.Id)
        };

        Genres.ReplaceRange(sorted);
    }

    [RelayCommand]
    private async Task PlayGenreAsync(Guid genreId)
    {
        if (IsLoading || genreId == Guid.Empty) return;

        await UserAction.RunAsync(() => _musicPlaybackService.PlayGenreAsync(genreId), _logger, _uiService);
    }

    [RelayCommand]
    private async Task PlayRandomGenreAsync()
    {
        if (IsLoading) return;

        await UserAction.RunAsync(async () =>
        {
            if (await _libraryReader.GetRandomGenreIdAsync() is { } id) await _musicPlaybackService.PlayGenreAsync(id);
        }, _logger, _uiService);
    }
}
