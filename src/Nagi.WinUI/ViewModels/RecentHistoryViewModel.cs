using System;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Data;
using Microsoft.Extensions.Logging;
using Nagi.Core.Services.Abstractions;
using Nagi.WinUI.Helpers;
using Nagi.WinUI.Models;
using Nagi.WinUI.Resources;
using Nagi.WinUI.Services.Abstractions;
using Windows.Foundation;

namespace Nagi.WinUI.ViewModels;

public partial class RecentHistoryViewModel : SearchableViewModelBase, IDisposable
{
    private readonly ILibraryReader _library;
    private readonly HistoryEntries _entries;
    private CancellationTokenSource? _loadCts;
    private bool _isDisposed;
    private bool _isActive;
    public ObservableRangeCollection<RecentListenEntry> Entries => _entries;

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

    private async Task<uint> LoadAsync(bool reset, CancellationToken token)
    {
        if (_isDisposed || !_isActive || token.IsCancellationRequested) return 0;
        if (reset) _entries.ResetLoading();
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        var cts = _loadCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        var loadToken = cts.Token;
        var searchTerm = SearchTerm;
        var offset = reset ? 0 : Entries.Count;
        IsLoading = true;
        HasLoadError = false;
        try
        {
            var listens = await Task.Run(() => _library.GetRecentListensAsync(searchTerm, offset, 100, loadToken), loadToken);
            if (cts.IsCancellationRequested || _isDisposed) return 0;
            var previousDay = reset ? (DateTime?)null : Entries.LastOrDefault()?.Listen.TimestampUtc.ToLocalTime().Date;
            var rows = listens.Select(listen =>
            {
                var day = listen.TimestampUtc.ToLocalTime().Date;
                var header = previousDay == day ? string.Empty : day == DateTime.Today ? Strings.Listening_Today
                    : day == DateTime.Today.AddDays(-1) ? Strings.Listening_Yesterday : day.ToString("D");
                previousDay = day;
                return new RecentListenEntry(listen, header);
            }).ToList();
            Entries.AppendOrReplace(rows, !reset);
            HasMore = listens.Count == 100;
            return (uint)rows.Count;
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
        : ObservableRangeCollection<RecentListenEntry>, ISupportIncrementalLoading
    {
        private Task<LoadMoreItemsResult>? _loadTask;
        public bool HasMoreItems => owner._isActive && !owner._isDisposed && owner.HasMore && !owner.IsLoading && !owner.HasLoadError;
        public void RefreshLoadingState() => OnPropertyChanged(new PropertyChangedEventArgs(nameof(HasMoreItems)));
        public void ResetLoading() => _loadTask = null;

        public IAsyncOperation<LoadMoreItemsResult> LoadMoreItemsAsync(uint count)
        {
            if (_loadTask?.IsCompleted != false) _loadTask = LoadPageAsync();
            return _loadTask.AsAsyncOperation();
        }

        private async Task<LoadMoreItemsResult> LoadPageAsync()
        {
            if (!HasMoreItems) return new LoadMoreItemsResult();
            return new LoadMoreItemsResult { Count = await owner.LoadAsync(false, CancellationToken.None) };
        }
    }
}
