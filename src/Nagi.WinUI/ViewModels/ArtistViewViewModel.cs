using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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
using Nagi.WinUI.Pages;
using Nagi.WinUI.Services.Abstractions;
using Nagi.WinUI.Helpers;
using Nagi.Core.Helpers;

namespace Nagi.WinUI.ViewModels;

/// <summary>
///     Represents a single album in the artist's discography list.
/// </summary>
public partial class ArtistAlbumViewModelItem : ObservableObject
{
    public ArtistAlbumViewModelItem(Album album)
    {
        Id = album.Id;
        Name = album.Title;
        YearText = album.Year?.ToString() ?? string.Empty;
        CoverArtUri = ImageUriHelper.GetUriWithCacheBuster(album.CoverArtUri);
    }

    public Guid Id { get; }
    public string Name { get; }
    public string? CoverArtUri { get; }
    public string YearText { get; }
    public bool IsArtworkAvailable => !string.IsNullOrEmpty(CoverArtUri);
}

/// <summary>
///     Provides data and commands for the artist details page, which displays an artist's
///     biography, albums, and a complete list of their songs.
/// </summary>
public partial class ArtistViewViewModel : SongListViewModelBase
{
    private readonly ILibraryScanner _libraryScanner;
    private readonly IUISettingsService _settingsService;
    private Guid _artistId;
    private bool _isNavigatingToAlbum;
    private CancellationTokenSource _pageCts = new();
    private readonly NotifyCollectionChangedEventHandler _albumsChangedHandler;

    public ArtistViewViewModel(
        ILibraryReader libraryReader,
        IPlaylistService playlistService,
        ILibraryScanner libraryScanner,
        IMusicPlaybackService playbackService,
        INavigationService navigationService,
        IMusicNavigationService musicNavigationService,
        IUISettingsService settingsService,
        IDispatcherService dispatcherService,
        IUIService uiService,
        ILogger<ArtistViewViewModel> logger)
        : base(libraryReader, playlistService, playbackService, navigationService, musicNavigationService, dispatcherService, uiService, logger)
    {
        _settingsService = settingsService;
        _libraryScanner = libraryScanner;

        // Initialize properties with default values
        ArtistName = Nagi.WinUI.Resources.Strings.ArtistView_DefaultArtistName;
        ArtistBio = Nagi.WinUI.Resources.Strings.Status_Loading;

        CurrentSortOrder = SongSortOrder.AlbumAsc;
        UpdateSortOrderButtonText(CurrentSortOrder);
        _albumsChangedHandler = (s, e) => OnPropertyChanged(nameof(HasAlbums));
        Albums.CollectionChanged += _albumsChangedHandler;
    }

    [ObservableProperty] public partial string ArtistName { get; set; }

    [ObservableProperty] public partial string ArtistBio { get; set; }

    [ObservableProperty] public partial string? ArtistImageUri { get; set; }

    partial void OnArtistImageUriChanged(string? value)
    {
        OnPropertyChanged(nameof(IsCustomImage));
    }

    public bool IsCustomImage => ArtistImageUri?.Contains(".custom.") == true;

    public ObservableRangeCollection<ArtistAlbumViewModelItem> Albums { get; } = new();
    public bool HasAlbums => Albums.Any();

    protected override async Task<PagedResult<Song>> LoadSongsPagedAsync(int pageNumber, int pageSize,
        SongSortOrder sortOrder)
    {
        if (_artistId == Guid.Empty) return new PagedResult<Song>();

        if (IsSearchActive)
            return await _libraryReader.SearchSongsInArtistPagedAsync(_artistId, SearchTerm, pageNumber, pageSize);

        return await _libraryReader.GetSongsByArtistIdPagedAsync(_artistId, pageNumber, pageSize, sortOrder);
    }

    protected override async Task<List<Guid>> LoadAllSongIdsAsync(SongSortOrder sortOrder)
    {
        if (_artistId == Guid.Empty) return new List<Guid>();

        if (IsSearchActive)
            return await _libraryReader.SearchAllSongIdsInArtistAsync(_artistId, SearchTerm, sortOrder);

        return await _libraryReader.GetAllSongIdsByArtistIdAsync(_artistId, sortOrder);
    }

    [RelayCommand]
    public async Task LoadArtistDetailsAsync(Guid artistId)
    {
        if (IsOverallLoading) return;
        _logger.LogDebug("Loading details for artist ID {ArtistId}", artistId);

        // Ensure we don't attach multiple handlers if this method is called again.
        _libraryScanner.ArtistMetadataUpdated -= OnArtistMetadataUpdated;
        _libraryScanner.ArtistMetadataBatchUpdated -= OnArtistMetadataBatchUpdated;
        _libraryScanner.ArtistMetadataUpdated += OnArtistMetadataUpdated;
        _libraryScanner.ArtistMetadataBatchUpdated += OnArtistMetadataBatchUpdated;

        try
        {
            _artistId = artistId;
            var onlineMetadataTask = _settingsService.GetFetchOnlineMetadataEnabledAsync();
            var sortOrderTask = _settingsService.GetSortOrderAsync<SongSortOrder>(SortOrderHelper.ArtistViewSortOrderKey);
            
            await Task.WhenAll(onlineMetadataTask, sortOrderTask);

            var shouldFetchOnline = onlineMetadataTask.Result;
            CurrentSortOrder = sortOrderTask.Result;

            // Start loading songs in parallel (updates its own UI)
            var songsTask = RefreshOrSortSongsCommand.ExecuteAsync(null);

            // Start loading artist details and albums in parallel
            var artistTask = _libraryScanner.GetArtistDetailsAsync(artistId, shouldFetchOnline, _pageCts.Token);
            var albumsTask = _libraryReader.GetTopAlbumsForArtistAsync(artistId, int.MaxValue);

            await Task.WhenAll(artistTask, albumsTask).ConfigureAwait(false);
            if (_pageCts.IsCancellationRequested) return;

            var artist = artistTask.Result;

            if (artist != null)
            {
                _dispatcherService.TryEnqueue(() =>
                {
                    PopulateArtistDetails(artist);

                    var topAlbums = albumsTask.Result;
                    Albums.ReplaceRange(topAlbums.Select(a => new ArtistAlbumViewModelItem(a)));
                });

                await songsTask.ConfigureAwait(false);
            }
            else
            {
                HandleArtistNotFound(artistId);
            }
        }
        catch (Exception ex)
        {
            HandleLoadError(artistId, ex);
        }
    }

    /// <summary>
    ///     Populates the ViewModel's properties from the Artist model.
    /// </summary>
    private void PopulateArtistDetails(Artist artist)
    {
        ArtistName = artist.Name;
        PageTitle = artist.Name;
        ArtistImageUri = ImageUriHelper.GetUriWithCacheBuster(artist.LocalImageCachePath);
        ArtistBio = string.IsNullOrWhiteSpace(artist.Biography)
            ? Nagi.WinUI.Resources.Strings.ArtistView_NoBiography
            : artist.Biography;

        _logger.LogDebug("Populated details for artist '{ArtistName}' ({ArtistId})", artist.Name, artist.Id);
    }

    /// <summary>
    ///     Handles the UI state when an artist cannot be found in the library.
    /// </summary>
    private void HandleArtistNotFound(Guid artistId)
    {
        _logger.LogWarning("Artist with ID {ArtistId} not found", artistId);
        ArtistName = Nagi.WinUI.Resources.Strings.ArtistView_ArtistNotFound;
        PageTitle = Nagi.WinUI.Resources.Strings.Generic_NoItems;
        ArtistBio = string.Empty;
        ArtistImageUri = null;
        Albums.Clear();
        Songs.Clear();
        TotalItemsText = ResourceFormatter.Format(Nagi.WinUI.Resources.Strings.Songs_Count_Plural, 0);
    }

    /// <summary>
    ///     Handles the UI state when an exception occurs during loading.
    /// </summary>
    private void HandleLoadError(Guid artistId, Exception ex)
    {
        _logger.LogError(ex, "Failed to load artist with ID {ArtistId}", artistId);
        ArtistName = Nagi.WinUI.Resources.Strings.ArtistView_Error;
        PageTitle = Nagi.WinUI.Resources.Strings.Generic_Error;
        ArtistBio = string.Empty;
        ArtistImageUri = null;
        TotalItemsText = Nagi.WinUI.Resources.Strings.Generic_Error;
        Albums.Clear();
        Songs.Clear();
    }

    [RelayCommand(CanExecute = nameof(CanExecuteLoadCommands))]
    private async Task ViewAlbumAsync(object? parameter)
    {
        if (_isNavigatingToAlbum) return;

        try
        {
            _isNavigatingToAlbum = true;
            await _musicNavigationService.NavigateToAlbumAsync(parameter);
        }
        finally
        {
            _isNavigatingToAlbum = false;
        }
    }

    /// <summary>
    ///     Handles real-time updates to artist metadata, such as a downloaded artist image.
    /// </summary>
    private void OnArtistMetadataUpdated(object? sender, ArtistMetadataUpdatedEventArgs e)
    {
        // Update the image only if it's for the currently displayed artist.
        if (e.ArtistId == _artistId)
        {
            _logger.LogDebug("Received metadata update for artist ID {ArtistId}", _artistId);
            // Must be run on the UI thread to update the bound property.
            _dispatcherService.TryEnqueue(() =>
            {
                // Guard against updates after the page has been cleaned up.
                // This prevents COMException crashes when navigating away quickly after an image update.
                if (_pageCts.IsCancellationRequested) return;
                
                // Force refresh by setting to null first, then apply cache-buster
                ArtistImageUri = null;
                ArtistImageUri = ImageUriHelper.GetUriWithCacheBuster(e.NewLocalImageCachePath);
            });
        }
    }

    /// <summary>
    ///     Handles batched updates to artist metadata.
    /// </summary>
    private void OnArtistMetadataBatchUpdated(object? sender, IEnumerable<ArtistMetadataUpdatedEventArgs> updates)
    {
        // Check if the current artist is in the batch
        var update = updates.FirstOrDefault(u => u.ArtistId == _artistId);
        if (update != null)
        {
            _logger.LogDebug("Received batch metadata update for artist ID {ArtistId}", _artistId);
            OnArtistMetadataUpdated(sender, update);
        }
    }


    protected override Task SaveSortOrderAsync(SongSortOrder sortOrder)
    {
        return _settingsService.SetSortOrderAsync(SortOrderHelper.ArtistViewSortOrderKey, sortOrder);
    }

    /// <summary>
    ///     Unsubscribes from events to prevent memory leaks.
    /// </summary>
    public override void ResetState()
    {
        _pageCts.Cancel();
        _pageCts.Dispose();

        base.ResetState();
        _logger.LogDebug("Cleaned up ArtistViewViewModel search resources");
        _libraryScanner.ArtistMetadataUpdated -= OnArtistMetadataUpdated;
        _libraryScanner.ArtistMetadataBatchUpdated -= OnArtistMetadataBatchUpdated;

        // Properly unsubscribe using the stored handler reference.
        Albums.CollectionChanged -= _albumsChangedHandler;
        _logger.LogDebug("Cleaned up for artist ID {ArtistId}", _artistId);
    }

    [RelayCommand]
    public async Task UpdateArtistImageAsync(string localPath)
    {
        if (string.IsNullOrWhiteSpace(localPath)) return;

        _logger.LogInformation("Updating custom image for current artist ({ArtistName}) from {Path}", ArtistName, localPath);
        
        // Use the library scanner's writer capability (it usually wraps ILibraryService)
        if (_libraryScanner is ILibraryWriter writer)
        {
            await writer.UpdateArtistImageAsync(_artistId, localPath);
        }
    }

    [RelayCommand]
    public async Task RemoveArtistImageAsync()
    {
        _logger.LogInformation("Removing custom image for current artist ({ArtistName})", ArtistName);
        
        if (_libraryScanner is ILibraryWriter writer)
        {
            await writer.RemoveArtistImageAsync(_artistId);
        }
    }
}