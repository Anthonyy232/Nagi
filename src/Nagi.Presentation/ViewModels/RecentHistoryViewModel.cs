using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Nagi.Core.Services.Abstractions;
using Nagi.Presentation.Helpers;
using Nagi.Presentation.Models;
using Nagi.Presentation.Resources;

namespace Nagi.Presentation.ViewModels;

public partial class RecentHistoryViewModel : SearchableViewModelBase, IDisposable
{
    private readonly ILibraryReader _library;
    private readonly HistoryEntries _entries;
    private CancellationTokenSource? _loadCts;
    private bool _isDisposed;
    private bool _isActive;
    public ObservableRangeCollection<object> Entries => _entries;

    public RecentHistoryViewModel(ILibraryReader library, IDispatcherService dispatcher, ILogger logger)
        : base(dispatcher, logger)
    {
        _library = library;
        _entries = new HistoryEntries(this);
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmptyState))]
    [NotifyPropertyChangedFor(nameof(ShowInitialLoading))]
    [NotifyPropertyChangedFor(nameof(ShowMoreLoading))]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmptyState))]
    public partial bool HasLoadError { get; set; }

    [ObservableProperty]
    public partial bool HasMore { get; set; }

    partial void OnIsLoadingChanged(bool value) => _entries.RefreshLoadingState();
    partial void OnHasMoreChanged(bool value) => _entries.RefreshLoadingState();
    partial void OnHasLoadErrorChanged(bool value) => _entries.RefreshLoadingState();

    public string EmptyStateText => IsSearchActive ? Strings.Listening_HistoryNoMatches : Strings.Listening_HistoryEmpty;

    public bool ShowEmptyState => !IsLoading && !HasLoadError && Entries.Count == 0;
    public bool ShowInitialLoading => IsLoading && Entries.Count == 0;
    public bool ShowMoreLoading => IsLoading && Entries.Count > 0;

    public void SetActive(bool active)
    {
        _isActive = active;
        _entries.RefreshLoadingState();
        if (active) _ = SearchAsync();
        else
        {
            CancelPendingSearch();
            _loadCts?.Cancel();
        }
    }

    protected override void OnSearchTermChangedInternal(string value)
    {
        _loadCts?.Cancel();
        OnPropertyChanged(nameof(EmptyStateText));
    }

    protected override Task ExecuteSearchAsync(CancellationToken token) =>
        _dispatcherService.EnqueueAsync(async () => { await LoadAsync(true, token); });

    public Task RefreshAsync() => _dispatcherService.EnqueueAsync(async () =>
    {
        if (!IsLoading) await LoadAsync(true, CancellationToken.None, preserveRows: true);
    });

    private async Task<int> LoadAsync(bool reset, CancellationToken token, bool preserveRows = false)
    {
        if (_isDisposed || !_isActive || token.IsCancellationRequested) return 0;
        if (reset) _entries.ResetLoading();
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        var cts = _loadCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        var loadToken = cts.Token;
        var searchTerm = SearchTerm;
        var loadedCount = Entries.OfType<RecentListenEntry>().Count();
        var newestListenId = Entries.OfType<RecentListenEntry>().FirstOrDefault()?.Listen.Id;
        var offset = reset ? 0 : loadedCount;
        IsLoading = true;
        HasLoadError = false;
        try
        {
            var (listens, songs) = await Task.Run(async () =>
            {
                var listens = await _library.GetRecentListensAsync(searchTerm, offset, 100, loadToken);
                if (preserveRows) listens = listens.TakeWhile(listen => listen.Id != newestListenId).ToArray();
                var songs = await _library.GetQueueSongsAsync(listens.Select(listen => listen.SongId), loadToken);
                return (listens, songs);
            }, loadToken);
            if (cts.IsCancellationRequested || _isDisposed) return 0;
            var previousDay = reset ? (DateTime?)null : Entries.OfType<RecentListenEntry>().LastOrDefault()?.Listen.TimestampUtc.ToLocalTime().Date;
            var rows = new List<object>();
            foreach (var listen in listens)
            {
                var day = listen.TimestampUtc.ToLocalTime().Date;
                if (previousDay != day)
                {
                    var title = day == DateTime.Today ? Strings.Listening_Today
                        : day == DateTime.Today.AddDays(-1) ? Strings.Listening_Yesterday : day.ToString("D");
                    rows.Add(new RecentListenDateHeader(day, title));
                }
                rows.Add(new RecentListenEntry(listen, songs.GetValueOrDefault(listen.SongId)));
                previousDay = day;
            }
            if (preserveRows)
            {
                if (rows.Count > 0 && Entries.FirstOrDefault() is RecentListenDateHeader header && header.Date == previousDay)
                    Entries.RemoveAt(0);
                for (var index = rows.Count - 1; index >= 0; index--) Entries.Insert(0, rows[index]);
            }
            else Entries.AppendOrReplace(rows, !reset);
            if (!preserveRows || loadedCount == 0) HasMore = listens.Count == 100;
            return rows.Count;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!cts.IsCancellationRequested && !_isDisposed)
            {
                HasLoadError = true;
                _logger.LogError(ex, "Failed to load recent listening history");
            }
        }
        finally
        {
            if (ReferenceEquals(_loadCts, cts))
            {
                IsLoading = false;
            }
        }
        return 0;
    }

    public void Dispose()
    {
        _isDisposed = true;
        ResetState();
        _loadCts?.Cancel();
        _loadCts?.Dispose();
    }

    private sealed class HistoryEntries(RecentHistoryViewModel owner)
        : ObservableRangeCollection<object>, IIncrementalItemSource
    {
        private Task<int>? _loadTask;
        public bool HasMoreItems => owner._isActive && !owner._isDisposed && owner.HasMore && !owner.IsLoading && !owner.HasLoadError;
        public void RefreshLoadingState() => OnPropertyChanged(new PropertyChangedEventArgs(nameof(HasMoreItems)));
        public void ResetLoading() => _loadTask = null;

        public Task<int> LoadMoreItemsAsync()
        {
            if (_loadTask?.IsCompleted != false) _loadTask = LoadPageAsync();
            return _loadTask;
        }

        private Task<int> LoadPageAsync() =>
            HasMoreItems ? owner.LoadAsync(false, CancellationToken.None) : Task.FromResult(0);
    }
}
