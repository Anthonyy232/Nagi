using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Nagi.Core.Artwork;
using Nagi.Core.Library;
using Nagi.Core.Platform;
using Nagi.Core.Playback;
using Nagi.Core.Settings;
using Nagi.Presentation.Common;
using Nagi.Presentation.Navigation;
using Nagi.Presentation.Resources;
using Nagi.Presentation.Settings;

namespace Nagi.Presentation.Lists;

public partial class ArtistViewModelItem : ObservableObject
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsArtworkAvailable), nameof(IsCustomImage))]
    public partial string? LocalImageCachePath { get; set; }

    public bool IsArtworkAvailable => !string.IsNullOrEmpty(LocalImageCachePath);
    public bool IsCustomImage => ImageStorageHelper.IsCustomImage(LocalImageCachePath);

    public override string ToString() => Name;
}

/// <summary>
///     Manages the artist list page. Only the current page is held in the VM; image
///     metadata is fetched for the whole library out-of-band via the library service.
/// </summary>
public partial class ArtistViewModel : PagedListViewModelBase<Artist>
{
    private readonly Dictionary<Guid, ArtistViewModelItem> _artistLookup = new();
    private readonly IArtworkService _artwork;
    private readonly IUIService _uiService;
    private readonly IMusicPlaybackService _musicPlaybackService;
    private readonly IMusicNavigationService _musicNavigationService;

    public ArtistViewModel(
        ILibraryReader libraryReader,
        ILibraryNotifier libraryNotifier,
        IArtworkService artwork,
        ISettingsService settingsService,
        IMusicPlaybackService musicPlaybackService,
        IDispatcherService dispatcherService,
        IMusicNavigationService musicNavigationService,
        IUIService uiService,
        ILogger<ArtistViewModel> logger)
        : base(libraryReader, libraryNotifier, settingsService, dispatcherService, logger)
    {
        _artwork = artwork;
        _uiService = uiService;
        _musicPlaybackService = musicPlaybackService;
        _musicNavigationService = musicNavigationService;
        Sort = new SortOrderSelection<ArtistSortOrder>(SortOrders, AppSettings.ArtistsSortOrder, settingsService, () =>
        {
            CurrentPage = 1;
            return LoadAsync(CancellationToken.None);
        }, logger);

        Artists.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasArtists));
        _libraryNotifier.ArtistImagesChanged += OnArtistImagesChanged;
    }

    public ObservableRangeCollection<ArtistViewModelItem> Artists { get; } = new();

    private static readonly ArtistSortOrder[] SortOrders =
        [ArtistSortOrder.NameAsc, ArtistSortOrder.NameDesc, ArtistSortOrder.SongCountAsc, ArtistSortOrder.SongCountDesc];

    public SortOrderSelection<ArtistSortOrder> Sort { get; }

    public bool HasArtists => Artists.Any();

    protected override string FormatTotalItemsText(PagedResult<Artist> result) =>
        result.TotalCount == 1
            ? ResourceFormatter.Format(Strings.Artists_Count_Singular, result.TotalCount)
            : ResourceFormatter.Format(Strings.Artists_Count_Plural, result.TotalCount);

    protected override Task<PagedResult<Artist>> LoadPageItemsAsync(int pageNumber, int pageSize, CancellationToken token) =>
        _libraryReader.GetArtistsAsync(SearchTerm, Sort.Current, pageNumber, pageSize, token);

    protected override void ApplyItemsToCollection(PagedResult<Artist> result, bool append)
    {
        if (!append) _artistLookup.Clear();
        var items = new List<ArtistViewModelItem>();
        foreach (var artist in result.Items)
        {
            var vm = new ArtistViewModelItem
            {
                Id = artist.Id,
                Name = artist.Name,
                LocalImageCachePath = artist.ImageUri
            };
            _artistLookup[artist.Id] = vm;
            items.Add(vm);
        }

        Artists.AppendOrReplace(items, append);
    }

    // Service's own semaphore dedupes concurrent runs of the metadata fetch.
    protected override Task OnPageLoadedAsync(PagedResult<Artist> result, CancellationToken token)
    {
        _ = _artwork.StartMetadataBackgroundFetchAsync();
        return Task.CompletedTask;
    }

    public void OpenArtist(ArtistViewModelItem artist) => _musicNavigationService.OpenArtist(artist.Id);

    [RelayCommand]
    private async Task PlayArtistAsync(Guid artistId)
    {
        if (IsLoading || artistId == Guid.Empty) return;
        await UserAction.RunAsync(() => _musicPlaybackService.PlayScopeAsync(new SongScope.Artist(artistId)), _logger, _uiService);
    }

    [RelayCommand]
    private async Task PlayRandomArtistAsync()
    {
        if (IsLoading) return;
        await UserAction.RunAsync(async () =>
        {
            if (await _libraryReader.GetRandomArtistIdAsync() is { } id) await _musicPlaybackService.PlayScopeAsync(new SongScope.Artist(id));
        }, _logger, _uiService);
    }

    private void OnArtistImagesChanged(object? sender, IReadOnlyList<ArtistImageChange> changes)
    {
        _dispatcherService.TryEnqueue(() =>
        {
            if (_isDisposed) return;
            foreach (var change in changes)
            {
                if (_artistLookup.TryGetValue(change.ArtistId, out var artistVm))
                    artistVm.LocalImageCachePath = change.ImageUri;
            }
        });
    }

    public override void Dispose()
    {
        _libraryNotifier.ArtistImagesChanged -= OnArtistImagesChanged;
        base.Dispose();
    }

    [RelayCommand]
    private Task ChangeArtistImageAsync(Guid artistId) =>
        ArtistImageActions.ChangeAsync(artistId, _uiService, _artwork, _logger, () => _isDisposed);

    [RelayCommand]
    private Task RemoveArtistImageAsync(Guid artistId) =>
        ArtistImageActions.RemoveAsync(artistId, _uiService, _artwork, _logger, () => _isDisposed);
}
