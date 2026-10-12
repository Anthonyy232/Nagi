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
using Nagi.WinUI.Services.Abstractions;
using Nagi.WinUI.Helpers;
using Nagi.Core.Helpers;

namespace Nagi.WinUI.ViewModels;

public sealed class ArtistAlbumViewModelItem
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
    private Guid _artistId;
    private bool _isNavigatingToAlbum;
    private CancellationTokenSource? _pageCts;

    public ArtistViewViewModel(
        ILibraryReader libraryReader,
        ILibraryWriter libraryWriter,
        ILibraryScanner libraryScanner,
        IPlaylistService playlistService,
        IMusicPlaybackService playbackService,
        IMusicNavigationService musicNavigationService,
        IUISettingsService settingsService,
        IDispatcherService dispatcherService,
        IUIService uiService,
        ILogger<ArtistViewViewModel> logger)
        : base(libraryReader, libraryWriter, libraryScanner, playlistService, playbackService, musicNavigationService, dispatcherService, settingsService, uiService, logger)
    {
        ArtistName = Nagi.WinUI.Resources.Strings.ArtistView_DefaultArtistName;
        ArtistBio = Nagi.WinUI.Resources.Strings.Status_Loading;

        CurrentSortOrder = SongSortOrder.AlbumAsc;
        Albums.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasAlbums));
    }

    [ObservableProperty] public partial string ArtistName { get; set; }

    [ObservableProperty] public partial string ArtistBio { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCustomImage))]
    public partial string? ArtistImageUri { get; set; }

    public bool IsCustomImage => ArtistImageUri?.Contains(".custom.") == true;

    public ObservableRangeCollection<ArtistAlbumViewModelItem> Albums { get; } = new();
    public bool HasAlbums => Albums.Any();

    protected override Task<PagedResult<Song>> LoadSongsPagedAsync(int pageNumber, int pageSize,
        SongSortOrder sortOrder, CancellationToken cancellationToken = default)
    {
        if (_artistId == Guid.Empty) return Task.FromResult(new PagedResult<Song>());

        if (IsSearchActive)
            return _libraryReader.SearchSongsInArtistPagedAsync(_artistId, SearchTerm, pageNumber, pageSize, cancellationToken);

        return _libraryReader.GetSongsByArtistIdPagedAsync(_artistId, pageNumber, pageSize, sortOrder, cancellationToken);
    }

    protected override Task<List<Guid>> LoadAllSongIdsAsync(SongSortOrder sortOrder, CancellationToken token = default)
    {
        if (_artistId == Guid.Empty) return Task.FromResult(new List<Guid>());

        if (IsSearchActive)
            return _libraryReader.SearchAllSongIdsInArtistAsync(_artistId, SearchTerm, sortOrder, token);

        return _libraryReader.GetAllSongIdsByArtistIdAsync(_artistId, sortOrder, token);
    }

    [RelayCommand]
    public async Task LoadArtistDetailsAsync(Guid artistId)
    {
        if (_isDisposed) return;
        ResetState();
        _pageCts = new CancellationTokenSource();
        var token = _pageCts.Token;
        _logger.LogDebug("Loading details for artist ID {ArtistId}", artistId);

        _libraryScanner.ArtistMetadataUpdated += OnArtistMetadataUpdated;
        _libraryScanner.ArtistMetadataBatchUpdated += OnArtistMetadataBatchUpdated;

        try
        {
            _artistId = artistId;
            var onlineMetadataTask = _settingsService.GetFetchOnlineMetadataEnabledAsync();
            var sortOrderTask = _settingsService.GetSortOrderAsync<SongSortOrder>(SortOrderHelper.ArtistViewSortOrderKey);

            await Task.WhenAll(onlineMetadataTask, sortOrderTask);
            token.ThrowIfCancellationRequested();

            var shouldFetchOnline = await onlineMetadataTask;
            CurrentSortOrder = await sortOrderTask;

            // Start loading songs in parallel (updates its own UI)
            var songsTask = RefreshOrSortSongsAsync(manualToken: token);

            // Start loading artist details and albums in parallel
            var artistTask = _libraryScanner.GetArtistDetailsAsync(artistId, shouldFetchOnline, token);
            var albumsTask = _libraryReader.GetTopAlbumsForArtistAsync(artistId, int.MaxValue);

            await Task.WhenAll(artistTask, albumsTask, songsTask);
            token.ThrowIfCancellationRequested();

            var artist = await artistTask;

            if (artist != null)
            {
                PopulateArtistDetails(artist);
                var topAlbums = await albumsTask;
                Albums.ReplaceRange(topAlbums.Select(a => new ArtistAlbumViewModelItem(a)));
            }
            else
            {
                HandleArtistNotFound(artistId);
            }
        }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested) HandleLoadError(artistId, ex);
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

    [RelayCommand(CanExecute = nameof(CanExecuteLoadCommands))]
    private async Task PlayAlbumAsync(Guid albumId)
    {
        await _playbackService.PlayAlbumAsync(albumId);
    }

    /// <summary>
    ///     Handles real-time updates to artist metadata, such as a downloaded artist image.
    /// </summary>
    private void OnArtistMetadataUpdated(object? sender, ArtistMetadataUpdatedEventArgs e)
    {
        if (e.ArtistId == _artistId)
        {
            var generation = CaptureSongContext();
            _logger.LogDebug("Received metadata update for artist ID {ArtistId}", _artistId);
            // Must be run on the UI thread to update the bound property.
            _dispatcherService.TryEnqueue(() =>
            {
                // Guard against updates after the page has been cleaned up.
                // This prevents COMException crashes when navigating away quickly after an image update.
                if (!IsSongContextCurrent(generation) || _pageCts is null) return;

                ArtistImageUri = ImageUriHelper.GetUriWithCacheBuster(e.NewLocalImageCachePath, DateTime.UtcNow);
            });
        }
    }

    /// <summary>
    ///     Handles batched updates to artist metadata.
    /// </summary>
    private void OnArtistMetadataBatchUpdated(object? sender, IEnumerable<ArtistMetadataUpdatedEventArgs> updates)
    {
        var update = updates.FirstOrDefault(u => u.ArtistId == _artistId);
        if (update != null)
        {
            _logger.LogDebug("Received batch metadata update for artist ID {ArtistId}", _artistId);
            OnArtistMetadataUpdated(sender, update);
        }
    }


    protected override PlaybackContext GetPlaybackContext() =>
        _artistId != Guid.Empty ? new(PlaybackContextType.Artist, _artistId) : base.GetPlaybackContext();

    protected override Task SaveSortOrderAsync(SongSortOrder sortOrder)
    {
        return _settingsService.SetSortOrderAsync(SortOrderHelper.ArtistViewSortOrderKey, sortOrder);
    }

    public override void ResetState()
    {
        _pageCts?.Cancel();
        _pageCts?.Dispose();
        _pageCts = null;

        base.ResetState();
        _logger.LogDebug("Cleaned up ArtistViewViewModel search resources");
        _libraryScanner.ArtistMetadataUpdated -= OnArtistMetadataUpdated;
        _libraryScanner.ArtistMetadataBatchUpdated -= OnArtistMetadataBatchUpdated;

        _logger.LogDebug("Cleaned up for artist ID {ArtistId}", _artistId);
    }

    [RelayCommand]
    public async Task UpdateArtistImageAsync(string localPath)
    {
        if (string.IsNullOrWhiteSpace(localPath)) return;

        _logger.LogInformation("Updating custom image for current artist ({ArtistName}) from {Path}", ArtistName, localPath);

        await _libraryWriter.UpdateArtistImageAsync(_artistId, localPath);
    }

    [RelayCommand]
    public Task RemoveArtistImageAsync()
    {
        _logger.LogInformation("Removing custom image for current artist ({ArtistName})", ArtistName);

        return _libraryWriter.RemoveArtistImageAsync(_artistId);
    }
}
