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
///     A display-optimized representation of an album for the user interface.
/// </summary>
public partial class AlbumViewModelItem : ObservableObject
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
    [ObservableProperty] public partial string Title { get; set; }
    [ObservableProperty] public partial string ArtistName { get; set; }
    public ICollection<AlbumArtist> AlbumArtists { get; }
    [ObservableProperty] public partial string? CoverArtUri { get; set; }

    public bool IsArtworkAvailable => !string.IsNullOrEmpty(CoverArtUri);

    partial void OnCoverArtUriChanged(string? value) => OnPropertyChanged(nameof(IsArtworkAvailable));
}

public partial class AlbumViewModel : PagedListViewModelBase
{
    private readonly NotifyCollectionChangedEventHandler _collectionChangedHandler;
    private readonly IMusicPlaybackService _musicPlaybackService;
    private readonly INavigationService _navigationService;
    private readonly IMusicNavigationService _musicNavigationService;

    private bool _isNavigating;

    public AlbumViewModel(ILibraryService libraryService, IMusicPlaybackService musicPlaybackService,
        INavigationService navigationService, IMusicNavigationService musicNavigationService,
        IUISettingsService settingsService, IDispatcherService dispatcherService, ILogger<AlbumViewModel> logger)
        : base(libraryService, settingsService, dispatcherService, logger)
    {
        _musicPlaybackService = musicPlaybackService;
        _navigationService = navigationService;
        _musicNavigationService = musicNavigationService;

        _collectionChangedHandler = (sender, args) => OnPropertyChanged(nameof(HasAlbums));
        Albums.CollectionChanged += _collectionChangedHandler;

        UpdateSortOrderText();
    }

    [ObservableProperty] public partial ObservableRangeCollection<AlbumViewModelItem> Albums { get; set; } = new();

    [ObservableProperty] public partial AlbumSortOrder CurrentSortOrder { get; set; } = AlbumSortOrder.ArtistAsc;

    [ObservableProperty] public partial string CurrentSortOrderText { get; set; } = string.Empty;

    partial void OnCurrentSortOrderChanged(AlbumSortOrder value) => UpdateSortOrderText();

    public bool HasAlbums => Albums.Any();

    private void UpdateSortOrderText() => CurrentSortOrderText = SortOrderHelper.GetDisplayName(CurrentSortOrder);

    protected override async Task LoadInitialSortOrderAsync() =>
        CurrentSortOrder = await _settingsService.GetSortOrderAsync<AlbumSortOrder>(SortOrderHelper.AlbumsSortOrderKey);

    protected override string FormatTotalItemsText(int count) =>
        count == 1
            ? ResourceFormatter.Format(Nagi.WinUI.Resources.Strings.Albums_Count_Singular, count)
            : ResourceFormatter.Format(Nagi.WinUI.Resources.Strings.Albums_Count_Plural, count);

    protected override async Task<int> LoadPageItemsAsync(int pageNumber, int pageSize, CancellationToken token)
    {
        var pagedResult = IsSearchActive
            ? await _libraryService.SearchAlbumsPagedAsync(SearchTerm, pageNumber, pageSize)
            : await _libraryService.GetAllAlbumsPagedAsync(pageNumber, pageSize, CurrentSortOrder);

        token.ThrowIfCancellationRequested();

        var items = pagedResult?.Items?.Select(a => new AlbumViewModelItem(a)).ToList() ?? new List<AlbumViewModelItem>();
        Albums.ReplaceRange(items);

        return pagedResult?.TotalCount ?? 0;
    }

    [RelayCommand]
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
        try { await _musicPlaybackService.PlayAlbumAsync(albumId); }
        catch (Exception ex) { _logger.LogCritical(ex, "Error playing album {AlbumId}", albumId); }
    }

    [RelayCommand]
    private async Task PlayRandomAlbumAsync()
    {
        if (IsLoading) return;
        try
        {
            var id = await _libraryService.GetRandomAlbumIdAsync();
            if (id.HasValue) await _musicPlaybackService.PlayAlbumAsync(id.Value);
        }
        catch (Exception ex) { _logger.LogCritical(ex, "Error playing random album"); }
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
        _ = _settingsService.SetSortOrderAsync(SortOrderHelper.AlbumsSortOrderKey, newSortOrder)
            .ContinueWith(t => _logger.LogError(t.Exception, "Failed to save album sort order"),
                TaskContinuationOptions.OnlyOnFaulted);
        CurrentPage = 1;
        await LoadCommand.ExecuteAsync(CancellationToken.None);
    }
}
