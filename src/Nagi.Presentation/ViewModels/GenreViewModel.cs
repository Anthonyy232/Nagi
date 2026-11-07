using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Nagi.Core.Library;
using Nagi.Core.Platform;
using Nagi.Core.Playback;
using Nagi.Core.Settings;
using Nagi.Presentation.Helpers;
using Nagi.Presentation.Services.Abstractions;
using Nagi.Presentation.Services;

namespace Nagi.Presentation.ViewModels;

public partial class GenreViewModel : ClientFilteredListViewModelBase<GenreSummary>
{
    private static readonly GenreSortOrder[] SortOrders =
        [GenreSortOrder.NameAsc, GenreSortOrder.NameDesc, GenreSortOrder.SongCountAsc, GenreSortOrder.SongCountDesc];

    private readonly ILibraryReader _libraryReader;
    private readonly IMusicPlaybackService _musicPlaybackService;
    private readonly IMusicNavigationService _navigationService;
    private readonly IUIService _uiService;

    public GenreViewModel(ILibraryReader libraryReader, ILibraryNotifier libraryNotifier, IMusicPlaybackService musicPlaybackService,
        IMusicNavigationService navigationService, ISettingsService settingsService, IDispatcherService dispatcherService, IUIService uiService,
        ILogger<GenreViewModel> logger)
        : base(libraryNotifier, dispatcherService, logger)
    {
        _libraryReader = libraryReader;
        _musicPlaybackService = musicPlaybackService;
        _navigationService = navigationService;
        _uiService = uiService;
        Sort = new SortOrderSelection<GenreSortOrder>(SortOrders, AppSettings.GenresSortOrder, settingsService, () =>
        {
            ApplyFilter();
            return Task.CompletedTask;
        }, logger);
    }

    public SortOrderSelection<GenreSortOrder> Sort { get; }

    // Song counts per genre change only when songs do.
    protected override bool ShouldReloadOnLibraryChange(LibraryChangeKind kind) =>
        kind is not (LibraryChangeKind.RootAdded or LibraryChangeKind.FavoritesChanged or LibraryChangeKind.ListenHistoryCleared);

    // SQLite's async APIs still perform native I/O, so keep the query off the UI thread.
    protected override async Task<IEnumerable<GenreSummary>> LoadItemsAsync(CancellationToken token) =>
        await Task.Run(() => _libraryReader.GetGenreSummariesAsync(token), token);

    protected override bool MatchesSearch(GenreSummary genre, string searchTerm) =>
        genre.Name.Contains(searchTerm, StringComparison.OrdinalIgnoreCase);

    protected override IEnumerable<GenreSummary> Arrange(IEnumerable<GenreSummary> genres) => Sort.Current switch
    {
        GenreSortOrder.NameDesc => genres.OrderByDescending(g => g.Name, StringComparer.OrdinalIgnoreCase).ThenBy(g => g.Id),
        GenreSortOrder.SongCountDesc => genres.OrderByDescending(g => g.SongCount).ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ThenBy(g => g.Id),
        GenreSortOrder.SongCountAsc => genres.OrderBy(g => g.SongCount).ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ThenBy(g => g.Id),
        _ => genres.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ThenBy(g => g.Id)
    };

    public void OpenGenre(GenreSummary genre) => _navigationService.OpenGenre(genre.Id, genre.Name);

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
