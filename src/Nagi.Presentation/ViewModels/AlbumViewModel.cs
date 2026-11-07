using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;
using Nagi.Presentation.Helpers;
using Nagi.Presentation.Resources;
using Nagi.Presentation.Services.Abstractions;
using Nagi.Presentation.Services;
using Nagi.Core.Settings;

namespace Nagi.Presentation.ViewModels;

public sealed class AlbumViewModelItem
{
    public AlbumViewModelItem(Album album)
    {
        Id = album.Id;
        Title = album.Title;
        ArtistName = album.ArtistName;
        AlbumArtists = album.AlbumArtists;
        CoverArtUri = ImageUriHelper.GetUriWithCacheBuster(album.CoverArtUri);
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

    private bool _isNavigating;

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

        Albums.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasAlbums));
    }

    public ObservableRangeCollection<AlbumViewModelItem> Albums { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentSortOrderText))]
    public partial AlbumSortOrder CurrentSortOrder { get; set; } = AlbumSortOrder.ArtistAsc;

    public string CurrentSortOrderText => SortOrderHelper.GetDisplayName(CurrentSortOrder);

    public bool HasAlbums => Albums.Any();

    protected override void LoadInitialSortOrder() =>
        CurrentSortOrder = _settingsService.Get(AppSettings.AlbumsSortOrder);

    protected override string FormatTotalItemsText(int count) =>
        count == 1
            ? ResourceFormatter.Format(Strings.Albums_Count_Singular, count)
            : ResourceFormatter.Format(Strings.Albums_Count_Plural, count);

    protected override Task<PagedResult<Album>> LoadPageItemsAsync(int pageNumber, int pageSize, CancellationToken token) =>
        _libraryReader.GetAlbumsAsync(SearchTerm, CurrentSortOrder, pageNumber, pageSize, token);

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

    public async Task NavigateToAlbumDetailAsync(object? parameter)
    {
        if (_isNavigating) return;
        try { _isNavigating = true; await _musicNavigationService.NavigateToAlbumAsync(parameter); }
        finally { _isNavigating = false; }
    }

    [RelayCommand]
    private async Task PlayAlbumAsync(Guid albumId)
    {
        if (IsLoading || albumId == Guid.Empty) return;
        await UserAction.RunAsync(() => _musicPlaybackService.PlayAlbumAsync(albumId), _logger, _uiService);
    }

    [RelayCommand]
    private async Task PlayRandomAlbumAsync()
    {
        if (IsLoading) return;
        await UserAction.RunAsync(async () =>
        {
            if (await _libraryReader.GetRandomAlbumIdAsync() is { } id) await _musicPlaybackService.PlayAlbumAsync(id);
        }, _logger, _uiService);
    }

    [RelayCommand]
    public async Task GoToArtistAsync(object? parameter)
    {
        if (_isNavigating) return;
        try { _isNavigating = true; await _musicNavigationService.NavigateToArtistAsync(parameter); }
        finally { _isNavigating = false; }
    }

    [RelayCommand]
    public async Task ChangeSortOrderAsync(string sortOrderString)
    {
        if (!Enum.TryParse<AlbumSortOrder>(sortOrderString, out var newSortOrder) || newSortOrder == CurrentSortOrder)
            return;

        CurrentSortOrder = newSortOrder;
        _settingsService.Set(AppSettings.AlbumsSortOrder, newSortOrder);
        CurrentPage = 1;
        await LoadCommand.ExecuteAsync(CancellationToken.None);
    }
}
