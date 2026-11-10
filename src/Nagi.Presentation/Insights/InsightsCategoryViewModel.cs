using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Nagi.Core.Listening;
using Nagi.Presentation.Common;
using Nagi.Presentation.Lists;

namespace Nagi.Presentation.Insights;

internal delegate Task<StatisticsPage<InsightsRankedItem>> RankingLoader(
    TimeRange range, SortMetric metric, int offset, int limit, string? searchTerm, CancellationToken ct);

/// <summary>
///     One ranking on the Insights page (songs, artists, albums or genres): its sort metric, its top items and the
///     way into its "See all" overlay.
/// </summary>
public sealed partial class InsightsCategoryViewModel
{
    private readonly InsightsViewModel _owner;
    private readonly RankingLoader _load;
    private CancellationTokenSource? _reloadCts;

    internal InsightsCategoryViewModel(InsightsViewModel owner, string title, SortMetric metric, RankingLoader load,
        ILogger logger)
    {
        _owner = owner;
        Title = title;
        _load = load;
        Sort = new SortOrderSelection<SortMetric>(Enum.GetValues<SortMetric>(), metric, _ => Task.CompletedTask,
            () => _owner.OnMetricChangedAsync(this), logger);
    }

    public string Title { get; }

    public SortOrderSelection<SortMetric> Sort { get; }

    public ObservableRangeCollection<InsightsRankedItem> TopItems { get; } = new();

    [RelayCommand]
    private Task OpenSeeAllAsync() => _owner.SeeAll.OpenAsync(this);

    internal Task<StatisticsPage<InsightsRankedItem>> LoadAsync(
        TimeRange range, int offset, int limit, string? searchTerm, CancellationToken ct) =>
        _load(range, Sort.Current, offset, limit, searchTerm, ct);

    internal CancellationToken RestartReload()
    {
        var newCts = new CancellationTokenSource();
        var oldCts = Interlocked.Exchange(ref _reloadCts, newCts);
        oldCts?.Cancel();
        oldCts?.Dispose();
        return newCts.Token;
    }

    internal void CancelReload() => Interlocked.Exchange(ref _reloadCts, null)?.Cancel();
}
