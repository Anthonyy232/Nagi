using System.Collections.Generic;
using System.Collections.Specialized;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Nagi.Core.Library;
using Nagi.Core.Playback;
using Nagi.Core.Playlists;
using Nagi.Core.Services.Abstractions;
using Nagi.Presentation.Navigation;
using Nagi.Presentation.Resources;
using Nagi.Presentation.Services.Abstractions;

namespace Nagi.Presentation.ViewModels;

public partial class PlaylistSongListViewModel : DetailSongListViewModelBase<PlaylistSongViewNavigationParameter>
{
    protected override bool PrefetchSongIds => true;

    private Guid? _currentPlaylistId;
    private bool _isSavingOrder;

    public PlaylistSongListViewModel(
        ILibraryReader libraryReader,
        IListeningService listening,
        ILibraryNotifier libraryNotifier,
        IPlaylistService playlistService,
        IMusicPlaybackService playbackService,
        IMusicNavigationService musicNavigationService,
        IDispatcherService dispatcherService,
        ISettingsService settingsService,
        IUIService uiService,
        ILogger<PlaylistSongListViewModel> logger)
        : base(libraryReader, listening, libraryNotifier, playlistService, playbackService, musicNavigationService, dispatcherService, settingsService, uiService, logger, [SongSortOrder.PlaylistOrder, .. CommonSortOrders], null)
    {
        Songs.CollectionChanged += OnSongsCollectionChanged;
    }

    // Reordering by dragging needs every song in the list.
    protected override bool IsPaged => false;

    public bool IsReorderingEnabled => !_isDisposed && !_isSavingOrder && !IsLoading && !HasLoadError &&
        CanEditPlaylist && Sort.Current == SongSortOrder.PlaylistOrder && !IsSearchActive &&
        Songs.Count == TotalItemCount;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveSelectedSongsFromPlaylistCommand))]
    [NotifyPropertyChangedFor(nameof(IsReorderingEnabled))]
    public partial bool CanEditPlaylist { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsArtworkAvailable))]
    public partial string? CoverImageUri { get; set; }

    public bool IsArtworkAvailable => !string.IsNullOrEmpty(CoverImageUri);

    protected override void OnSearchTermChangedInternal(string value)
    {
        base.OnSearchTermChangedInternal(value);
        OnPropertyChanged(nameof(IsReorderingEnabled));
    }

    protected override Task OnSortOrderChangedAsync()
    {
        OnPropertyChanged(nameof(IsReorderingEnabled));
        return base.OnSortOrderChangedAsync();
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

    protected override async Task<bool> OpenDetailsAsync(PlaylistSongViewNavigationParameter parameter, CancellationToken token)
    {
        Sort.Current = SongSortOrder.PlaylistOrder;
        PageTitle = parameter.Title;
        _currentPlaylistId = parameter.PlaylistId;
        CoverImageUri = parameter.CoverImageUri;
        var playlist = await _libraryReader.GetPlaylistByIdAsync(parameter.PlaylistId);
        if (token.IsCancellationRequested) return true;
        // A server's playlist follows the server at each sync, so it can't be changed here.
        CanEditPlaylist = playlist?.IsFromServer != true;
        return true;
    }

    protected override Task<PagedResult<Song>> LoadSongsPagedAsync(int pageNumber, int pageSize,
        SongSortOrder sortOrder, CancellationToken cancellationToken = default)
    {
        if (!_currentPlaylistId.HasValue) return Task.FromResult(new PagedResult<Song>());

        return _libraryReader.GetSongsAsync(new SongScope.Playlist(_currentPlaylistId.Value), SearchTerm, sortOrder,
            pageNumber, pageSize, cancellationToken);
    }

    protected override Task<List<Guid>> LoadAllSongIdsAsync(SongSortOrder sortOrder, CancellationToken token = default)
    {
        if (!_currentPlaylistId.HasValue) return Task.FromResult(new List<Guid>());

        return _libraryReader.GetSongIdsAsync(new SongScope.Playlist(_currentPlaylistId.Value), SearchTerm, sortOrder, token);
    }

    protected override PlaybackContext GetPlaybackContext() =>
        _currentPlaylistId.HasValue ? new(PlaybackContextType.Playlist, _currentPlaylistId.Value) : base.GetPlaybackContext();

    [RelayCommand(CanExecute = nameof(CanRemoveSongs))]
    private Task RemoveSelectedSongsFromPlaylistAsync() =>
        !_currentPlaylistId.HasValue || !HasSelectedSongs ? Task.CompletedTask : RunActionAsync(async () =>
        {
            var playlistId = _currentPlaylistId.Value;
            var token = OpenToken;
            var songIdsToRemove = await GetCurrentSelectionIdsAsync();
            if (_isDisposed || token.IsCancellationRequested || songIdsToRemove.Count == 0) return;
            _logger.LogDebug("Removing {SongCount} songs from playlist ID {PlaylistId}", songIdsToRemove.Count,
                playlistId);

            var success =
                await _playlistService.RemoveSongsFromPlaylistAsync(playlistId, songIdsToRemove);
            if (success && !_isDisposed && !token.IsCancellationRequested)
                await LoadAsync(CancellationToken.None);
        });

    public override ICommand RemoveSelectionCommand => RemoveSelectedSongsFromPlaylistCommand;

    private bool CanRemoveSongs()
    {
        return CanEditPlaylist && HasSelectedSongs;
    }

    /// <summary>Persists the completed drag, including every item in a multi-selection.</summary>
    public async Task OnSongsReorderedAsync(IReadOnlyCollection<Song> movedSongs)
    {
        if (!IsReorderingEnabled || !_currentPlaylistId.HasValue || movedSongs.Count == 0) return;
        var songs = Songs.ToList();
        var orderedIds = songs.Select(song => song.Id).ToList();
        var visibleIds = orderedIds.ToHashSet();
        if (movedSongs.Any(song => !visibleIds.Contains(song.Id)) || !ReplaceCachedSongIds(orderedIds)) return;
        var playlistId = _currentPlaylistId.Value;
        var token = OpenToken;
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

        var saved = false;
        try
        {
            saved = normalize
                ? await _playlistService.UpdatePlaylistOrderAsync(playlistId, orderedIds)
                : await _playlistService.MovePlaylistSongAsync(playlistId, moved.Id, newOrder);
            if (saved && !_isDisposed && !token.IsCancellationRequested)
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
            if (!saved && !_isDisposed && !token.IsCancellationRequested)
            {
                await LoadAsync(CancellationToken.None);
                if (!_isDisposed && !token.IsCancellationRequested)
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
        _currentPlaylistId = null;
        CanEditPlaylist = false;

        base.ResetState();
    }

    public override void Dispose()
    {
        if (_isDisposed) return;
        Songs.CollectionChanged -= OnSongsCollectionChanged;
        base.Dispose();
    }
}
