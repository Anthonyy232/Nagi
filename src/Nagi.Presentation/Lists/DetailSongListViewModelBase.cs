using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Nagi.Core.Library;
using Nagi.Core.Listening;
using Nagi.Core.Platform;
using Nagi.Core.Playback;
using Nagi.Core.Playlists;
using Nagi.Core.Settings;
using Nagi.Presentation.Navigation;
using Nagi.Presentation.Resources;
using Nagi.Presentation.Services.Abstractions;

namespace Nagi.Presentation.Lists;

/// <summary>
///     A song list a page opens from its navigation parameter.
/// </summary>
public interface IDetailListViewModel
{
    Task OpenAsync(INavigationParameter parameter);
}

/// <summary>
///     The songs of one album, artist, genre, folder or playlist, opened from the parameter that names it.
/// </summary>
public abstract class DetailSongListViewModelBase<TParameter> : SongListViewModelBase, IDetailListViewModel
    where TParameter : INavigationParameter
{
    private CancellationTokenSource _openCts = new();

    protected DetailSongListViewModelBase(ILibraryReader libraryReader, IListeningService listening,
        ILibraryNotifier libraryNotifier, IPlaylistService playlistService, IMusicPlaybackService playbackService,
        IMusicNavigationService musicNavigationService, IDispatcherService dispatcherService,
        ISettingsService settingsService, IUIService uiService, ILogger logger,
        IReadOnlyList<SongSortOrder> sortOrders, Setting<SongSortOrder>? sortOrderSetting)
        : base(libraryReader, listening, libraryNotifier, playlistService, playbackService, musicNavigationService,
            dispatcherService, settingsService, uiService, logger, sortOrders, sortOrderSetting)
    {
    }

    /// <summary>Cancelled when the list opens something else or closes, so late results of the open can be dropped.</summary>
    protected CancellationToken OpenToken => _openCts.Token;

    /// <summary>
    ///     Whether the details load beside the songs. Otherwise the songs load only once the details are found.
    /// </summary>
    protected virtual bool LoadsSongsWithDetails => false;

    /// <summary>The title shown when the parameter names nothing in the library.</summary>
    protected virtual string NotFoundTitle => Strings.Generic_NoItems;

    /// <summary>The title shown when opening failed, or null to keep the one from the parameter.</summary>
    protected virtual string? OpenErrorTitle => null;

    /// <summary>Reads what the parameter names and fills in the header.</summary>
    /// <returns>False when the library has nothing by that name.</returns>
    protected abstract Task<bool> OpenDetailsAsync(TParameter parameter, CancellationToken token);

    /// <summary>Clears the header after a failed or empty open.</summary>
    protected virtual void ClearDetails()
    {
    }

    Task IDetailListViewModel.OpenAsync(INavigationParameter parameter) => OpenAsync((TParameter)parameter);

    public async Task OpenAsync(TParameter parameter)
    {
        if (_isDisposed) return;
        ResetState();
        var token = OpenToken;
        CurrentPage = 1;
        TotalItemCount = 0;
        Songs.Clear();
        _logger.LogDebug("Opening {Parameter}", parameter);

        try
        {
            var details = OpenDetailsAsync(parameter, token);
            var songs = LoadsSongsWithDetails ? LoadAsync(token) : Task.CompletedTask;
            await Task.WhenAll(details, songs);
            if (token.IsCancellationRequested || _isDisposed) return;

            if (!details.Result)
            {
                _logger.LogWarning("Could not find {Parameter}", parameter);
                PageTitle = NotFoundTitle;
                TotalItemsText = FormatTotalItemsText(new PagedResult<Song>());
                Songs.Clear();
                ClearDetails();
                return;
            }

            if (!LoadsSongsWithDetails) await LoadAsync(token);
        }
        catch (Exception ex)
        {
            if (token.IsCancellationRequested || _isDisposed) return;
            _logger.LogError(ex, "Failed to open {Parameter}", parameter);
            if (OpenErrorTitle is { } title) PageTitle = title;
            TotalItemsText = LoadErrorText;
            HasLoadError = true;
            Songs.Clear();
            ClearDetails();
        }
    }

    public override void ResetState()
    {
        _openCts.Cancel();
        _openCts.Dispose();
        _openCts = new CancellationTokenSource();
        base.ResetState();
    }
}
