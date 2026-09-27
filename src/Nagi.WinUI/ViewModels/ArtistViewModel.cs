using System;
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
using Nagi.WinUI.Navigation;
using Nagi.WinUI.Services.Abstractions;
using Nagi.Core.Helpers;
using Nagi.WinUI.Helpers;

namespace Nagi.WinUI.ViewModels;

/// <summary>
///     A display-optimized representation of an artist for the user interface.
/// </summary>
public partial class ArtistViewModelItem : ObservableObject
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsArtworkAvailable), nameof(IsCustomImage))]
    public partial string? LocalImageCachePath { get; set; }

    public bool IsArtworkAvailable => !string.IsNullOrEmpty(LocalImageCachePath);
    public bool IsCustomImage => LocalImageCachePath?.Contains(".custom.") == true;

}

/// <summary>
///     Manages the artist list page. Only the current page is held in the VM; image
///     metadata is fetched for the whole library out-of-band via the library service.
/// </summary>
public partial class ArtistViewModel : PagedListViewModelBase<Artist>
{
    private readonly Dictionary<Guid, ArtistViewModelItem> _artistLookup = new();
    private readonly IMusicPlaybackService _musicPlaybackService;
    private readonly IMusicNavigationService _musicNavigationService;

    private bool _isNavigating;

    public ArtistViewModel(
        ILibraryService libraryService,
        IUISettingsService settingsService,
        IMusicPlaybackService musicPlaybackService,
        IDispatcherService dispatcherService,
        IMusicNavigationService musicNavigationService,
        ILogger<ArtistViewModel> logger)
        : base(libraryService, settingsService, dispatcherService, logger)
    {
        _musicPlaybackService = musicPlaybackService;
        _musicNavigationService = musicNavigationService;

        Artists.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasArtists));
    }

    public ObservableRangeCollection<ArtistViewModelItem> Artists { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentSortOrderText))]
    public partial ArtistSortOrder CurrentSortOrder { get; set; } = ArtistSortOrder.NameAsc;

    public string CurrentSortOrderText => SortOrderHelper.GetDisplayName(CurrentSortOrder);

    public bool HasArtists => Artists.Any();

    protected override async Task LoadInitialSortOrderAsync() =>
        CurrentSortOrder = await _settingsService.GetSortOrderAsync<ArtistSortOrder>(SortOrderHelper.ArtistsSortOrderKey);

    protected override string FormatTotalItemsText(int count) =>
        count == 1
            ? ResourceFormatter.Format(Nagi.WinUI.Resources.Strings.Artists_Count_Singular, count)
            : ResourceFormatter.Format(Nagi.WinUI.Resources.Strings.Artists_Count_Plural, count);

    protected override Task<PagedResult<Artist>> LoadPageItemsAsync(int pageNumber, int pageSize, CancellationToken token) =>
        IsSearchActive
            ? _libraryService.SearchArtistsPagedAsync(SearchTerm, pageNumber, pageSize, token)
            : _libraryService.GetAllArtistsPagedAsync(pageNumber, pageSize, CurrentSortOrder, token);

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
                LocalImageCachePath = ImageUriHelper.GetUriWithCacheBuster(artist.LocalImageCachePath)
            };
            _artistLookup[artist.Id] = vm;
            items.Add(vm);
        }

        Artists.AppendOrReplace(items, append);
    }

    // Service's own semaphore dedupes concurrent runs of the metadata fetch.
    protected override async Task OnPageLoadedAsync(PagedResult<Artist> result, CancellationToken token)
    {
        if (await _settingsService.GetFetchOnlineMetadataEnabledAsync())
            _ = _libraryService.StartArtistMetadataBackgroundFetchAsync();
    }

    [RelayCommand]
    public async Task NavigateToArtistDetailAsync(object? parameter)
    {
        if (_isNavigating) return;
        try { _isNavigating = true; await _musicNavigationService.NavigateToArtistAsync(parameter); }
        finally { _isNavigating = false; }
    }

    [RelayCommand]
    private async Task PlayArtistAsync(Guid artistId)
    {
        if (IsLoading || artistId == Guid.Empty) return;
        try { await _musicPlaybackService.PlayArtistAsync(artistId); }
        catch (Exception ex) { _logger.LogCritical(ex, "Error playing artist {ArtistId}", artistId); }
    }

    [RelayCommand]
    private async Task PlayRandomArtistAsync()
    {
        if (IsLoading) return;
        try
        {
            var id = await _libraryService.GetRandomArtistIdAsync();
            if (id.HasValue) await _musicPlaybackService.PlayArtistAsync(id.Value);
        }
        catch (Exception ex) { _logger.LogCritical(ex, "Error playing random artist"); }
    }

    [RelayCommand]
    public async Task ChangeSortOrderAsync(string sortOrderString)
    {
        if (!Enum.TryParse<ArtistSortOrder>(sortOrderString, out var newSortOrder) || newSortOrder == CurrentSortOrder)
            return;

        CurrentSortOrder = newSortOrder;
        try { await _settingsService.SetSortOrderAsync(SortOrderHelper.ArtistsSortOrderKey, newSortOrder); }
        catch (Exception ex) { _logger.LogError(ex, "Failed to save artist sort order"); }
        CurrentPage = 1;
        await LoadCommand.ExecuteAsync(CancellationToken.None);
    }

    private void OnArtistMetadataUpdated(object? sender, ArtistMetadataUpdatedEventArgs e)
    {
        _dispatcherService.TryEnqueue(() =>
        {
            if (_isDisposed) return;
            if (_artistLookup.TryGetValue(e.ArtistId, out var artistVm))
                artistVm.LocalImageCachePath = ImageUriHelper.GetUriWithCacheBuster(e.NewLocalImageCachePath, DateTime.UtcNow);
        });
    }

    private void OnArtistMetadataBatchUpdated(object? sender, IEnumerable<ArtistMetadataUpdatedEventArgs> updates)
    {
        _dispatcherService.TryEnqueue(() =>
        {
            if (_isDisposed) return;
            var timestamp = DateTime.UtcNow;
            foreach (var update in updates)
            {
                if (_artistLookup.TryGetValue(update.ArtistId, out var artistVm))
                    artistVm.LocalImageCachePath = ImageUriHelper.GetUriWithCacheBuster(update.NewLocalImageCachePath, timestamp);
            }
        });
    }

    public void SubscribeToEvents()
    {
        _libraryService.ArtistMetadataUpdated += OnArtistMetadataUpdated;
        _libraryService.ArtistMetadataBatchUpdated += OnArtistMetadataBatchUpdated;
    }

    public void UnsubscribeFromEvents()
    {
        _libraryService.ArtistMetadataUpdated -= OnArtistMetadataUpdated;
        _libraryService.ArtistMetadataBatchUpdated -= OnArtistMetadataBatchUpdated;
    }

    public override void Dispose()
    {
        UnsubscribeFromEvents();
        base.Dispose();
    }

    [RelayCommand]
    public async Task UpdateArtistImageAsync(Tuple<Guid, string> artistData)
    {
        if (artistData == null) return;
        var (artistId, localPath) = artistData;
        await _libraryService.UpdateArtistImageAsync(artistId, localPath);
    }

    [RelayCommand]
    public Task RemoveArtistImageAsync(Guid artistId) => _libraryService.RemoveArtistImageAsync(artistId);
}
