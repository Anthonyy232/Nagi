using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nagi.Core.Services.Abstractions;
using Nagi.Presentation.Helpers;
using Nagi.Presentation.Resources;

namespace Nagi.Presentation.ViewModels;

internal delegate Task<StatisticsPage<InsightsRankedItem>> RankingLoader(
    TimeRange range, SortMetric metric, int offset, int limit, string? searchTerm, CancellationToken ct);

/// <summary>
///     One ranking on the Insights page (songs, artists, albums or genres): its sort metric, its top items and the
///     way into its "See all" overlay.
/// </summary>
public sealed partial class InsightsCategoryViewModel : ObservableObject
{
    private readonly InsightsViewModel _owner;
    private readonly RankingLoader _load;
    private SortMetric _metric;
    private CancellationTokenSource? _reloadCts;

    internal InsightsCategoryViewModel(InsightsViewModel owner, string title, SortMetric metric, RankingLoader load)
    {
        _owner = owner;
        Title = title;
        _metric = metric;
        _load = load;
    }

    public string Title { get; }

    public SortMetric Metric
    {
        get => _metric;
        private set
        {
            if (!SetProperty(ref _metric, value)) return;
            OnPropertyChanged(nameof(IsByPlayCount));
            OnPropertyChanged(nameof(IsByDuration));
            OnPropertyChanged(nameof(SortLabel));
            _ = _owner.OnMetricChangedAsync(this);
        }
    }

    public bool IsByPlayCount => Metric == SortMetric.PlayCount;
    public bool IsByDuration => Metric == SortMetric.Duration;

    public string SortLabel => Metric == SortMetric.PlayCount
        ? Strings.InsightsPage_SortBy_PlayCount
        : Strings.InsightsPage_SortBy_Duration;

    public ObservableRangeCollection<InsightsRankedItem> TopItems { get; } = new();

    [RelayCommand]
    private void SetMetric(string metricName)
    {
        if (Enum.TryParse<SortMetric>(metricName, out var metric)) Metric = metric;
    }

    [RelayCommand]
    private Task OpenSeeAllAsync() => _owner.OpenSeeAllAsync(this);

    internal Task<StatisticsPage<InsightsRankedItem>> LoadAsync(
        TimeRange range, int offset, int limit, string? searchTerm, CancellationToken ct) =>
        _load(range, Metric, offset, limit, searchTerm, ct);

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
