using System;
using System.Collections.Generic;
using System.Collections.Specialized;
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
    [ObservableProperty] public partial Guid Id { get; set; }
    [ObservableProperty] public partial string Name { get; set; } = string.Empty;
    [ObservableProperty] public partial string? LocalImageCachePath { get; set; }

    public bool IsArtworkAvailable => !string.IsNullOrEmpty(LocalImageCachePath);
    public bool IsCustomImage => LocalImageCachePath?.Contains(".custom.") == true;

    partial void OnLocalImageCachePathChanged(string? value)
    {
        OnPropertyChanged(nameof(IsArtworkAvailable));
        OnPropertyChanged(nameof(IsCustomImage));
    }
}

/// <summary>
///     Manages the artist list page. Only the current page is held in the VM; image
///     metadata is fetched for the whole library out-of-band via the library service.
/// </summary>
public partial class ArtistViewModel : PagedListViewModelBase<Artist>
{
    private readonly Dictionary<Guid, ArtistViewModelItem> _artistLookup = new();
    private readonly NotifyCollectionChangedEventHandler _collectionChangedHandler;
    private readonly IMusicPlaybackService _musicPlaybackService;
    private readonly INavigationService _navigationService;
    private readonly IMusicNavigationService _musicNavigationService;

    private bool _isNavigating;

    public ArtistViewModel(
        ILibraryService libraryService,
        IUISettingsService settingsService,
        IMusicPlaybackService musicPlaybackService,
        IDispatcherService dispatcherService,
        INavigationService navigationService,
        IMusicNavigationService musicNavigationService,
        ILogger<ArtistViewModel> logger)
        : base(libraryService, settingsService, dispatcherService, logger)
    {
        _musicPlaybackService = musicPlaybackService;
        _navigationService = navigationService;
        _musicNavigationService = musicNavigationService;

        _collectionChangedHandler = (s, e) => OnPropertyChanged(nameof(HasArtists));
        Artists.CollectionChanged += _collectionChangedHandler;

        UpdateSortOrderText();
    }

    [ObservableProperty] public partial ObservableRangeCollection<ArtistViewModelItem> Artists { get; set; } = new();

    [ObservableProperty] public partial ArtistSortOrder CurrentSortOrder { get; set; } = ArtistSortOrder.NameAsc;

    [ObservableProperty] public partial string CurrentSortOrderText { get; set; } = string.Empty;

    public bool HasArtists => Artists.Any();

    partial void OnCurrentSortOrderChanged(ArtistSortOrder value) => UpdateSortOrderText();

    private void UpdateSortOrderText() => CurrentSortOrderText = SortOrderHelper.GetDisplayName(CurrentSortOrder);

    protected override async Task LoadInitialSortOrderAsync() =>
        CurrentSortOrder = await _settingsService.GetSortOrderAsync<ArtistSortOrder>(SortOrderHelper.ArtistsSortOrderKey);

    protected override string FormatTotalItemsText(int count) =>
        count == 1
            ? ResourceFormatter.Format(Nagi.WinUI.Resources.Strings.Artists_Count_Singular, count)
            : ResourceFormatter.Format(Nagi.WinUI.Resources.Strings.Artists_Count_Plural, count);

    protected override async Task<PagedResult<Artist>> LoadPageItemsAsync(int pageNumber, int pageSize, CancellationToken token)
    {
        var pagedResult = IsSearchActive
            ? await _libraryService.SearchArtistsPagedAsync(SearchTerm, pageNumber, pageSize)
            : await _libraryService.GetAllArtistsPagedAsync(pageNumber, pageSize, CurrentSortOrder);

        token.ThrowIfCancellationRequested();
        return pagedResult ?? new PagedResult<Artist>();
    }

    protected override void ApplyItemsToCollection(PagedResult<Artist> result, bool append)
    {
        if (result?.Items == null) return;

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
        _ = _settingsService.SetSortOrderAsync(SortOrderHelper.ArtistsSortOrderKey, newSortOrder)
            .ContinueWith(t => _logger.LogError(t.Exception, "Failed to save artist sort order"),
                TaskContinuationOptions.OnlyOnFaulted);
        CurrentPage = 1;
        await LoadCommand.ExecuteAsync(CancellationToken.None);
    }

    private void OnArtistMetadataUpdated(object? sender, ArtistMetadataUpdatedEventArgs e)
    {
        if (!_artistLookup.TryGetValue(e.ArtistId, out var artistVm)) return;
        _dispatcherService.TryEnqueue(() =>
        {
            artistVm.LocalImageCachePath = ImageUriHelper.GetUriWithCacheBuster(e.NewLocalImageCachePath, DateTime.UtcNow);
        });
    }

    private void OnArtistMetadataBatchUpdated(object? sender, IEnumerable<ArtistMetadataUpdatedEventArgs> updates)
    {
        _dispatcherService.TryEnqueue(() =>
        {
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

    [RelayCommand]
    public async Task UpdateArtistImageAsync(Tuple<Guid, string> artistData)
    {
        if (artistData == null) return;
        var (artistId, localPath) = artistData;
        await _libraryService.UpdateArtistImageAsync(artistId, localPath);
    }

    [RelayCommand]
    public async Task RemoveArtistImageAsync(Guid artistId) =>
        await _libraryService.RemoveArtistImageAsync(artistId);
}
