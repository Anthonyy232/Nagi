using System.Collections.Generic;
using System.Collections.Specialized;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;
using Nagi.Presentation.Resources;
using Nagi.Presentation.Services.Abstractions;

namespace Nagi.Presentation.ViewModels;

public partial class PlaylistSongListViewModel : SongListViewModelBase
{
    protected override bool PrefetchSongIds => true;

    private Guid? _currentPlaylistId;
    private bool _isSavingOrder;
    private int _playlistGeneration;

    public PlaylistSongListViewModel(
        ILibraryReader libraryReader,
        ILibraryWriter libraryWriter,
        ILibraryScanner libraryScanner,
        IPlaylistService playlistService,
        IMusicPlaybackService playbackService,
        IMusicNavigationService musicNavigationService,
        IDispatcherService dispatcherService,
        IUISettingsService settingsService,
        IUIService uiService,
        ILogger<PlaylistSongListViewModel> logger)
        : base(libraryReader, libraryWriter, libraryScanner, playlistService, playbackService, musicNavigationService, dispatcherService, settingsService, uiService, logger)
    {
        IsPaginationEnabled = false;
        Songs.CollectionChanged += OnSongsCollectionChanged;
    }


    public bool IsReorderingEnabled => !_isDisposed && !_isSavingOrder && !IsLoading && !HasLoadError &&
        IsCurrentViewAPlaylist && CurrentSortOrder == SongSortOrder.PlaylistOrder && !IsSearchActive &&
        Songs.Count == TotalItemCount;



    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveSelectedSongsFromPlaylistCommand))]
    [NotifyPropertyChangedFor(nameof(IsReorderingEnabled))]
    public partial bool IsCurrentViewAPlaylist { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsArtworkAvailable))]
    public partial string? CoverImageUri { get; set; }

    public bool IsArtworkAvailable => !string.IsNullOrEmpty(CoverImageUri);

    protected override void OnSearchTermChangedInternal(string value)
    {
        base.OnSearchTermChangedInternal(value);
        OnPropertyChanged(nameof(IsReorderingEnabled));
    }

    protected override void OnCurrentSortOrderChangedInternal(SongSortOrder oldOrder, SongSortOrder newOrder)
    {
        base.OnCurrentSortOrderChangedInternal(oldOrder, newOrder);
        OnPropertyChanged(nameof(IsReorderingEnabled));
    }

    protected override void OnLoadingStateChanged(bool isLoading)
    {
        base.OnLoadingStateChanged(isLoading);
        OnPropertyChanged(nameof(IsReorderingEnabled));
    }

    protected override void UpdateSelectionStatus()
    {
        base.UpdateSelectionStatus();
        RemoveSelectedSongsFromPlaylistCommand.NotifyCanExecuteChanged();
    }

    public async Task InitializeAsync(string title, Guid playlistId, string? coverImageUri = null)
    {
        if (_isDisposed) return;
        ResetState();
        CurrentSortOrder = SongSortOrder.PlaylistOrder;
        _logger.LogDebug("Initializing for playlist '{Title}' (ID: {PlaylistId})", title, playlistId);

        try
        {
            PageTitle = title;
            _currentPlaylistId = playlistId;
            IsCurrentViewAPlaylist = true;
            CoverImageUri = coverImageUri;

            await RefreshOrSortSongsCommand.ExecuteAsync(null);
            OnPropertyChanged(nameof(IsReorderingEnabled));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize playlist {PlaylistId}", _currentPlaylistId);
            TotalItemsText = Strings.Playlist_ErrorLoading;
            Songs.Clear();
        }
    }


    protected override Task<PagedResult<Song>> LoadSongsPagedAsync(int pageNumber, int pageSize,
        SongSortOrder sortOrder, CancellationToken cancellationToken = default)
    {
        if (!_currentPlaylistId.HasValue) return Task.FromResult(new PagedResult<Song>());

        if (IsSearchActive)
            return _libraryReader.SearchSongsInPlaylistPagedAsync(_currentPlaylistId.Value, SearchTerm, pageNumber, pageSize, sortOrder, cancellationToken);

        return _libraryReader.GetSongsByPlaylistPagedAsync(_currentPlaylistId.Value, pageNumber, pageSize, sortOrder, cancellationToken);
    }

    protected override Task<List<Guid>> LoadAllSongIdsAsync(SongSortOrder sortOrder, CancellationToken token = default)
    {
        if (!_currentPlaylistId.HasValue) return Task.FromResult(new List<Guid>());

        if (IsSearchActive)
            return _libraryReader.SearchAllSongIdsInPlaylistAsync(_currentPlaylistId.Value, SearchTerm, sortOrder, token);

        return _libraryReader.GetAllSongIdsByPlaylistIdAsync(_currentPlaylistId.Value, sortOrder, token);
    }

    protected override PlaybackContext GetPlaybackContext() =>
        _currentPlaylistId.HasValue ? new(PlaybackContextType.Playlist, _currentPlaylistId.Value) : base.GetPlaybackContext();

    [RelayCommand(CanExecute = nameof(CanRemoveSongs))]
    private Task RemoveSelectedSongsFromPlaylistAsync() =>
        !_currentPlaylistId.HasValue || !HasSelectedSongs ? Task.CompletedTask : RunActionAsync(async () =>
        {
            var playlistId = _currentPlaylistId.Value;
            var generation = _playlistGeneration;
            var songIdsToRemove = await GetCurrentSelectionIdsAsync();
            if (_isDisposed || generation != _playlistGeneration || songIdsToRemove.Count == 0) return;
            _logger.LogDebug("Removing {SongCount} songs from playlist ID {PlaylistId}", songIdsToRemove.Count,
                playlistId);

            var success =
                await _playlistService.RemoveSongsFromPlaylistAsync(playlistId, songIdsToRemove);
            if (success && !_isDisposed && generation == _playlistGeneration)
                await RefreshOrSortSongsCommand.ExecuteAsync(null);
        });

    private bool CanRemoveSongs()
    {
        return IsCurrentViewAPlaylist && HasSelectedSongs;
    }

    /// <summary>Persists the completed drag, including every item in a multi-selection.</summary>
    public async Task OnSongsReorderedAsync(IReadOnlyCollection<Song> movedSongs)
    {
        if (!IsReorderingEnabled || !_currentPlaylistId.HasValue || movedSongs.Count == 0) return;
        var songs = Songs.ToList();
        var orderedIds = songs.Select(song => song.Id).ToList();
        var visibleIds = orderedIds.ToHashSet();
        if (movedSongs.Any(song => !visibleIds.Contains(song.Id))) return;
        lock (_fullSongIdsLock)
        {
            if (_fullSongIdList.SequenceEqual(orderedIds)) return;
        }
        var playlistId = _currentPlaylistId.Value;
        var generation = _playlistGeneration;
        var normalize = movedSongs.Count > 1;
        var moved = movedSongs.First();
        var newOrder = moved.Order;

        if (!normalize)
        {
            var index = songs.FindIndex(song => song.Id == moved.Id);
            var previous = index > 0 ? songs[index - 1].Order : double.NegativeInfinity;
            var next = index < songs.Count - 1 ? songs[index + 1].Order : double.PositiveInfinity;
            newOrder = double.IsNegativeInfinity(previous)
                ? double.IsPositiveInfinity(next) ? 1 : next > 0.5 ? next / 2 : next - 1
                : double.IsPositiveInfinity(next) ? Math.Floor(previous) + 1
                : previous + (next - previous) / 2;
            // Normalize immediately when fractional positions can no longer represent this move.
            // The new integer positions must also be reflected in memory before the next drag.
            normalize = !double.IsFinite(newOrder) || newOrder <= previous || newOrder >= next ||
                (double.IsFinite(previous) && double.IsFinite(next) && next - previous < 1e-9);
        }

        _isSavingOrder = true;
        OnPropertyChanged(nameof(IsReorderingEnabled));
        lock (_fullSongIdsLock)
        {
            _fullSongIdList.Clear();
            _fullSongIdList.AddRange(orderedIds);
        }

        var saved = false;
        try
        {
            saved = normalize
                ? await _playlistService.UpdatePlaylistOrderAsync(playlistId, orderedIds)
                : await _playlistService.MovePlaylistSongAsync(playlistId, moved.Id, newOrder);
            if (saved && !_isDisposed && generation == _playlistGeneration)
            {
                if (normalize)
                    for (var i = 0; i < songs.Count; i++) songs[i].Order = i + 1;
                else
                    moved.Order = newOrder;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save song order for playlist {PlaylistId}", playlistId);
        }
        try
        {
            if (!saved && !_isDisposed && generation == _playlistGeneration)
            {
                await RefreshOrSortSongsAsync();
                if (!_isDisposed && generation == _playlistGeneration)
                    await _uiService.ShowMessageDialogAsync(
                        Strings.Generic_Error, Strings.Playlist_ErrorSavingOrder);
            }
        }
        finally
        {
            _isSavingOrder = false;
            OnPropertyChanged(nameof(IsReorderingEnabled));
        }
    }

    private void OnSongsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Do not disable an active drag during the intermediate Remove events.
        if (Songs.Count == TotalItemCount) OnPropertyChanged(nameof(IsReorderingEnabled));
    }

    public override void ResetState()
    {
        _logger.LogDebug("Cleaning up PlaylistSongListViewModel resources");

        _playlistGeneration++;
        _currentPlaylistId = null;
        IsCurrentViewAPlaylist = false;

        base.ResetState();
    }

    public override void Dispose()
    {
        if (_isDisposed) return;
        Songs.CollectionChanged -= OnSongsCollectionChanged;
        base.Dispose();
    }
}
