using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Nagi.Core.Constants;
using Nagi.Core.Helpers;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;
using Nagi.Presentation.Helpers;
using Nagi.Presentation.Resources;
using Nagi.Presentation.Services.Abstractions;

namespace Nagi.Presentation.ViewModels;

public partial class ArtistViewModelItem : ObservableObject
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsArtworkAvailable), nameof(IsCustomImage))]
    public partial string? LocalImageCachePath { get; set; }

    public bool IsArtworkAvailable => !string.IsNullOrEmpty(LocalImageCachePath);
    public bool IsCustomImage => LocalImageCachePath?.Contains(".custom.") == true;


    public override string ToString() => Name;
}

/// <summary>
///     Manages the artist list page. Only the current page is held in the VM; image
///     metadata is fetched for the whole library out-of-band via the library service.
/// </summary>
public partial class ArtistViewModel : PagedListViewModelBase<Artist>
{
    private readonly Dictionary<Guid, ArtistViewModelItem> _artistLookup = new();
    private readonly ILibraryWriter _libraryWriter;
    private readonly IUIService _uiService;
    private readonly IMusicPlaybackService _musicPlaybackService;
    private readonly IMusicNavigationService _musicNavigationService;

    private bool _isNavigating;

    public ArtistViewModel(
        ILibraryReader libraryReader,
        ILibraryWriter libraryWriter,
        ILibraryScanner libraryScanner,
        IUISettingsService settingsService,
        IMusicPlaybackService musicPlaybackService,
        IDispatcherService dispatcherService,
        IMusicNavigationService musicNavigationService,
        IUIService uiService,
        ILogger<ArtistViewModel> logger)
        : base(libraryReader, libraryScanner, settingsService, dispatcherService, logger)
    {
        _libraryWriter = libraryWriter;
        _uiService = uiService;
        _musicPlaybackService = musicPlaybackService;
        _musicNavigationService = musicNavigationService;

        Artists.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasArtists));
        _libraryScanner.ArtistMetadataUpdated += OnArtistMetadataUpdated;
        _libraryScanner.ArtistMetadataBatchUpdated += OnArtistMetadataBatchUpdated;
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
            ? ResourceFormatter.Format(Strings.Artists_Count_Singular, count)
            : ResourceFormatter.Format(Strings.Artists_Count_Plural, count);

    protected override Task<PagedResult<Artist>> LoadPageItemsAsync(int pageNumber, int pageSize, CancellationToken token) =>
        IsSearchActive
            ? _libraryReader.SearchArtistsPagedAsync(SearchTerm, pageNumber, pageSize, token)
            : _libraryReader.GetAllArtistsPagedAsync(pageNumber, pageSize, CurrentSortOrder, token);

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
    protected override Task OnPageLoadedAsync(PagedResult<Artist> result, CancellationToken token)
    {
        _ = _libraryScanner.StartMetadataBackgroundFetchAsync();
        return Task.CompletedTask;
    }

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
            var id = await _libraryReader.GetRandomArtistIdAsync();
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
        await _settingsService.SetSortOrderAsync(SortOrderHelper.ArtistsSortOrderKey, newSortOrder);
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

    public override void Dispose()
    {
        _libraryScanner.ArtistMetadataUpdated -= OnArtistMetadataUpdated;
        _libraryScanner.ArtistMetadataBatchUpdated -= OnArtistMetadataBatchUpdated;
        base.Dispose();
    }

    /// <summary>
    ///     Lets the user pick an image file and stores it as the artist's custom image.
    /// </summary>
    [RelayCommand]
    private async Task ChangeArtistImageAsync(Guid artistId)
    {
        try
        {
            var localPath = await _uiService.PickSingleFileAsync(FileExtensions.ImageFileExtensions);
            if (string.IsNullOrWhiteSpace(localPath)) return;
            await _libraryWriter.UpdateArtistImageAsync(artistId, localPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to change the image of artist {ArtistId}", artistId);
        }
    }

    [RelayCommand]
    private async Task RemoveArtistImageAsync(Guid artistId)
    {
        try { await _libraryWriter.RemoveArtistImageAsync(artistId); }
        catch (Exception ex) { _logger.LogError(ex, "Failed to remove the image of artist {ArtistId}", artistId); }
    }
}
