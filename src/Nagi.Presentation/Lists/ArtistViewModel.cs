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
    [NotifyPropertyChangedFor(nameof(IsCustomImage))]
    public partial string? ImageUri { get; set; }

    public bool IsCustomImage => ImageStorageHelper.IsCustomImage(ImageUri);

    public override string ToString() => Name;
}

/// <summary>
///     Manages the artist list page. Only the current page is held in the VM; image
///     metadata for the whole library is fetched in the background by <see cref="IArtworkService" />.
/// </summary>
public partial class ArtistViewModel : PagedListViewModelBase<Artist>, ICollectionViewModel
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
        _libraryNotifier.ArtistImagesChanged += OnArtistImagesChanged;
    }

    public ObservableRangeCollection<ArtistViewModelItem> Items { get; } = new();

    private static readonly ArtistSortOrder[] SortOrders =
        [ArtistSortOrder.NameAsc, ArtistSortOrder.NameDesc, ArtistSortOrder.SongCountAsc, ArtistSortOrder.SongCountDesc];

    public SortOrderSelection<ArtistSortOrder> Sort { get; }

    protected override string FormatTotalItemsText(PagedResult<Artist> result) =>
        ResourceFormatter.Format(Strings.Artists_Count, result.TotalCount);

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
                ImageUri = artist.ImageUri
            };
            _artistLookup[artist.Id] = vm;
            items.Add(vm);
        }

        Items.AppendOrReplace(items, append);
    }

    // Service's own semaphore dedupes concurrent runs of the metadata fetch.
    protected override void OnPageLoaded() => _ = _artwork.StartMetadataBackgroundFetchAsync();

    public void OpenArtist(ArtistViewModelItem artist) => _musicNavigationService.OpenArtist(artist.Id);

    void ICollectionViewModel.Open(object item) => OpenArtist((ArtistViewModelItem)item);

    ISortOrderSelection? ICollectionViewModel.Sort => Sort;

    [RelayCommand]
    private async Task PlayArtistAsync(Guid artistId)
    {
        if (IsLoading || artistId == Guid.Empty) return;
        await UserAction.RunAsync(() => _musicPlaybackService.PlayScopeAsync(new SongScope.Artist(artistId)), _logger, _uiService);
    }

    [RelayCommand]
    private async Task PlayRandomAsync()
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
                    artistVm.ImageUri = change.ImageUri;
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
