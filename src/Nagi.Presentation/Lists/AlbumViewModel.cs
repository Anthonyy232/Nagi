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

public sealed class AlbumViewModelItem
{
    public AlbumViewModelItem(Album album)
    {
        Id = album.Id;
        Title = album.Title;
        ArtistName = album.ArtistName;
        AlbumArtists = album.AlbumArtists;
        CoverArtUri = album.CoverArtUri;
    }

    public Guid Id { get; }
    public string Title { get; }
    public string ArtistName { get; }
    public ICollection<AlbumArtist> AlbumArtists { get; }
    public string? CoverArtUri { get; }

    public bool IsArtworkAvailable => !string.IsNullOrEmpty(CoverArtUri);

    public override string ToString() => $"{Title} — {ArtistName}";
}

public partial class AlbumViewModel : PagedListViewModelBase<Album>
{
    private readonly IArtworkService _artwork;
    private readonly IMusicPlaybackService _musicPlaybackService;
    private readonly IMusicNavigationService _musicNavigationService;
    private readonly IUIService _uiService;

    public AlbumViewModel(ILibraryReader libraryReader, ILibraryNotifier libraryNotifier, IArtworkService artwork,
        IMusicPlaybackService musicPlaybackService,
        IMusicNavigationService musicNavigationService,
        ISettingsService settingsService, IDispatcherService dispatcherService, IUIService uiService, ILogger<AlbumViewModel> logger)
        : base(libraryReader, libraryNotifier, settingsService, dispatcherService, logger)
    {
        _artwork = artwork;
        _musicPlaybackService = musicPlaybackService;
        _musicNavigationService = musicNavigationService;
        _uiService = uiService;
        Sort = new SortOrderSelection<AlbumSortOrder>(SortOrders, AppSettings.AlbumsSortOrder, settingsService, () =>
        {
            CurrentPage = 1;
            return LoadAsync(CancellationToken.None);
        }, logger);

        Albums.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasAlbums));
    }

    public ObservableRangeCollection<AlbumViewModelItem> Albums { get; } = new();

    private static readonly AlbumSortOrder[] SortOrders =
    [
        AlbumSortOrder.ArtistAsc, AlbumSortOrder.ArtistDesc, AlbumSortOrder.AlbumTitleAsc, AlbumSortOrder.AlbumTitleDesc,
        AlbumSortOrder.YearAsc, AlbumSortOrder.YearDesc, AlbumSortOrder.SongCountAsc, AlbumSortOrder.SongCountDesc
    ];

    public SortOrderSelection<AlbumSortOrder> Sort { get; }

    public bool HasAlbums => Albums.Any();

    protected override string FormatTotalItemsText(PagedResult<Album> result) =>
        ResourceFormatter.Format(Strings.Albums_Count, result.TotalCount);

    protected override Task<PagedResult<Album>> LoadPageItemsAsync(int pageNumber, int pageSize, CancellationToken token) =>
        _libraryReader.GetAlbumsAsync(SearchTerm, Sort.Current, pageNumber, pageSize, token);

    protected override void ApplyItemsToCollection(PagedResult<Album> result, bool append)
    {
        Albums.AppendOrReplace(result.Items.Select(a => new AlbumViewModelItem(a)), append);
    }

    // Service's own semaphore dedupes concurrent runs of the metadata fetch.
    protected override Task OnPageLoadedAsync(PagedResult<Album> result, CancellationToken token)
    {
        _ = _artwork.StartMetadataBackgroundFetchAsync();
        return Task.CompletedTask;
    }

    public void OpenAlbum(AlbumViewModelItem album) => _musicNavigationService.OpenAlbum(album.Id);

    [RelayCommand]
    private async Task PlayAlbumAsync(Guid albumId)
    {
        if (IsLoading || albumId == Guid.Empty) return;
        await UserAction.RunAsync(() => _musicPlaybackService.PlayScopeAsync(new SongScope.Album(albumId)), _logger, _uiService);
    }

    [RelayCommand]
    private async Task PlayRandomAlbumAsync()
    {
        if (IsLoading) return;
        await UserAction.RunAsync(async () =>
        {
            if (await _libraryReader.GetRandomAlbumIdAsync() is { } id) await _musicPlaybackService.PlayScopeAsync(new SongScope.Album(id));
        }, _logger, _uiService);
    }

    [RelayCommand]
    private Task GoToArtistAsync(ArtistNavigationRequest request) => _musicNavigationService.OpenArtistAsync(request);
}
