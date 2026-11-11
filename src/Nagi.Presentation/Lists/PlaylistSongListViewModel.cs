using System.Collections.Specialized;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Nagi.Core.Library;
using Nagi.Core.Listening;
using Nagi.Core.Platform;
using Nagi.Core.Playback;
using Nagi.Core.Playlists;
using Nagi.Core.Settings;
using Nagi.Presentation.Common;
using Nagi.Presentation.Navigation;
using Nagi.Presentation.Resources;

namespace Nagi.Presentation.Lists;

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
    public partial string? CoverImageUri { get; set; }

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
        var playlist = await _playlistService.GetPlaylistByIdAsync(parameter.PlaylistId);
        if (token.IsCancellationRequested) return true;
        // A server's playlist follows the server at each sync, so it can't be changed here.
        CanEditPlaylist = playlist?.IsFromServer != true;
        return true;
    }

    protected override SongScope? Scope =>
        _currentPlaylistId is { } playlistId ? new SongScope.Playlist(playlistId) : null;

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
        var orderedIds = Songs.Select(song => song.Id).ToList();
        var visibleIds = orderedIds.ToHashSet();
        if (movedSongs.Any(song => !visibleIds.Contains(song.Id)) || !ReplaceCachedSongIds(orderedIds)) return;
        var playlistId = _currentPlaylistId.Value;
        var token = OpenToken;

        _isSavingOrder = true;
        OnPropertyChanged(nameof(IsReorderingEnabled));

        var saved = false;
        try
        {
            if (movedSongs.Count > 1)
            {
                saved = await _playlistService.UpdatePlaylistOrderAsync(playlistId, orderedIds);
            }
            else
            {
                var index = orderedIds.IndexOf(movedSongs.First().Id);
                saved = await _playlistService.MoveSongAsync(playlistId, orderedIds[index],
                    index > 0 ? orderedIds[index - 1] : null, index < orderedIds.Count - 1 ? orderedIds[index + 1] : null);
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
