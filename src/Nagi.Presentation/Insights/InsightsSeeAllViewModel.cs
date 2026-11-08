using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Nagi.Core.Listening;
using Nagi.Core.Platform;
using Nagi.Presentation.Common;
using Nagi.Presentation.Lists;
using Nagi.Presentation.Settings;

namespace Nagi.Presentation.Insights;

/// <summary>
///     The Insights "See all" overlay: one category's whole ranking, searchable and paged like the other lists.
/// </summary>
public sealed partial class InsightsSeeAllViewModel : SearchableViewModelBase, IPagedListViewModel
{
    private readonly Func<TimeRange> _timeRange;
    private CancellationTokenSource? _loadCts;

    internal InsightsSeeAllViewModel(Func<TimeRange> timeRange, IDispatcherService dispatcherService, ILogger logger)
        : base(dispatcherService, logger)
    {
        _timeRange = timeRange;
    }

    /// <summary>The category shown, or <c>null</c> while the overlay is closed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOpen))]
    public partial InsightsCategoryViewModel? Category { get; private set; }

    public bool IsOpen => Category is not null;

    [ObservableProperty] public partial bool IsLoading { get; private set; }

    public ObservableRangeCollection<InsightsRankedItem> Items { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNextPage), nameof(HasPreviousPage))]
    [NotifyCanExecuteChangedFor(nameof(NextPageCommand), nameof(PreviousPageCommand))]
    public partial int CurrentPage { get; private set; } = 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNextPage))]
    [NotifyCanExecuteChangedFor(nameof(NextPageCommand))]
    public partial int TotalPages { get; private set; } = 1;

    /// <summary>The items a page holds, or 0 to show every item.</summary>
    [ObservableProperty] public partial int PageSize { get; set; } = AppSettings.PageSizeOptions[0];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPaginate))]
    public partial int TotalItemCount { get; private set; }

    public bool CanPaginate => TotalItemCount > AppSettings.PageSizeOptions[0];
    public bool HasNextPage => CurrentPage < TotalPages;
    public bool HasPreviousPage => CurrentPage > 1;

    partial void OnPageSizeChanged(int value) => _ = ReloadAsync();

    internal Task OpenAsync(InsightsCategoryViewModel category) => ShowPageAsync(category, 1, 1);

    private Task ReloadAsync() => Category is { } category ? OpenAsync(category) : Task.CompletedTask;

    protected override Task ExecuteSearchAsync(CancellationToken token) =>
        _dispatcherService.EnqueueAsync(() => token.IsCancellationRequested ? Task.CompletedTask : ReloadAsync());

    [RelayCommand(CanExecute = nameof(HasNextPage))]
    private Task NextPageAsync() =>
        Category is { } category ? ShowPageAsync(category, CurrentPage + 1, CurrentPage) : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(HasPreviousPage))]
    private Task PreviousPageAsync() =>
        Category is { } category ? ShowPageAsync(category, CurrentPage - 1, CurrentPage) : Task.CompletedTask;

    [RelayCommand]
    private async Task CloseAsync()
    {
        _loadCts?.Cancel();
        CancelPendingSearch();

        await _dispatcherService.EnqueueAsync(() =>
        {
            Category = null;
            SearchTerm = string.Empty;
            IsLoading = false;
            return Task.CompletedTask;
        });
    }

    private async Task ShowPageAsync(InsightsCategoryViewModel category, int page, int previousPage)
    {
        var newCts = new CancellationTokenSource();
        var ct = newCts.Token;
        var oldCts = Interlocked.Exchange(ref _loadCts, newCts);
        oldCts?.Cancel();
        oldCts?.Dispose();

        try
        {
            await _dispatcherService.EnqueueAsync(() =>
            {
                ct.ThrowIfCancellationRequested();
                Category = category;
                CurrentPage = page;
                IsLoading = true;
                return Task.CompletedTask;
            });

            var limit = PageSize == 0 ? int.MaxValue : PageSize;
            var result = await category.LoadAsync(_timeRange(), (page - 1) * limit, limit, SearchTerm, ct);
            await _dispatcherService.EnqueueAsync(() =>
            {
                ct.ThrowIfCancellationRequested();
                TotalItemCount = result.TotalCount;
                TotalPages = Math.Max(1, (int)Math.Ceiling(result.TotalCount / (double)limit));
                Items.ReplaceRange(result.Items);
                return Task.CompletedTask;
            });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load see-all data for {Category}, page {Page}", category.Title, page);
            await _dispatcherService.EnqueueAsync(() =>
            {
                if (!ct.IsCancellationRequested) CurrentPage = previousPage;
                return Task.CompletedTask;
            });
        }
        finally
        {
            await _dispatcherService.EnqueueAsync(() =>
            {
                if (!ct.IsCancellationRequested) IsLoading = false;
                return Task.CompletedTask;
            });
        }
    }
}
