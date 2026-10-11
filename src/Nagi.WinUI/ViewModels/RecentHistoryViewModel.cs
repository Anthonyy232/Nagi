using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Nagi.Core.Services.Abstractions;
using Nagi.WinUI.Helpers;
using Nagi.WinUI.Models;
using Nagi.WinUI.Resources;
using Nagi.WinUI.Services.Abstractions;

namespace Nagi.WinUI.ViewModels;

public partial class RecentHistoryViewModel(ILibraryReader library, IDispatcherService dispatcher, ILogger logger)
    : SearchableViewModelBase(dispatcher, logger), IDisposable
{
    private CancellationTokenSource? _loadCts;
    private bool _isDisposed;
    private bool _isActive;
    public ObservableRangeCollection<RecentListenEntry> Entries { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadMoreCommand))]
    [NotifyPropertyChangedFor(nameof(ShowEmptyState))]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmptyState))]
    public partial bool HasLoadError { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadMoreCommand))]
    public partial bool HasMore { get; set; }

    public string EmptyStateText => IsSearchActive ? Strings.Listening_HistoryNoMatches : Strings.Listening_HistoryEmpty;

    public bool ShowEmptyState => !IsLoading && !HasLoadError && Entries.Count == 0;

    public void SetActive(bool active)
    {
        _isActive = active;
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
        _dispatcherService.EnqueueAsync(() => LoadAsync(true, token));

    private bool CanLoadMore() => HasMore && !IsLoading;

    [RelayCommand(CanExecute = nameof(CanLoadMore))]
    private Task LoadMoreAsync() => LoadAsync(false, CancellationToken.None);

    private async Task LoadAsync(bool reset, CancellationToken token)
    {
        if (_isDisposed || !_isActive || token.IsCancellationRequested) return;
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
            var listens = await Task.Run(() => library.GetRecentListensAsync(searchTerm, offset, 100, loadToken), loadToken);
            if (cts.IsCancellationRequested || _isDisposed) return;
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
    }

    public void Dispose()
    {
        _isDisposed = true;
        ResetState();
        _loadCts?.Cancel();
        _loadCts?.Dispose();
    }
}
