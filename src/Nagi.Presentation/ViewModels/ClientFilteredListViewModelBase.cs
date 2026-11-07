using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Nagi.Core.Library;
using Nagi.Core.Platform;
using Nagi.Presentation.Common;

namespace Nagi.Presentation.ViewModels;

/// <summary>
///     A list small enough to load whole: it loads every item, reloads shortly after the library changes, and filters
///     and sorts in memory.
/// </summary>
public abstract partial class ClientFilteredListViewModelBase<TItem> : SearchableViewModelBase, IDisposable
{
    private readonly ILibraryNotifier _libraryNotifier;
    private readonly Debouncer _libraryChangeDebouncer = new(TimeSpan.FromSeconds(1));
    private CancellationTokenSource? _loadCts;
    private int _loadVersion;
    protected bool _isDisposed;
    private bool _hasLoaded;

    protected ClientFilteredListViewModelBase(ILibraryNotifier libraryNotifier, IDispatcherService dispatcherService,
        ILogger logger)
        : base(dispatcherService, logger)
    {
        _libraryNotifier = libraryNotifier;
        _libraryNotifier.Changed += OnLibraryChanged;
        Items.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasItems));
            OnPropertyChanged(nameof(ShowEmptyState));
        };
    }

    /// <summary>The items the search and sort let through.</summary>
    public ObservableRangeCollection<TItem> Items { get; } = new();

    public bool HasItems => Items.Count > 0;

    /// <summary>Whether a load has finished and nothing passes the search, so the list doesn't look empty while it loads.</summary>
    public bool ShowEmptyState => _hasLoaded && !IsLoading && !HasLoadError && Items.Count == 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmptyState))]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmptyState))]
    public partial bool HasLoadError { get; set; }

    /// <summary>Every loaded item, before the search and sort.</summary>
    protected List<TItem> AllItems { get; private set; } = [];

    protected abstract Task<IEnumerable<TItem>> LoadItemsAsync(CancellationToken token);

    protected abstract bool ShouldReloadOnLibraryChange(LibraryChangeKind kind);

    protected virtual bool MatchesSearch(TItem item, string searchTerm) => true;

    /// <summary>Puts the items in the order the list shows them.</summary>
    protected abstract IEnumerable<TItem> Arrange(IEnumerable<TItem> items);

    protected virtual void OnLoadingChanged() { }

    partial void OnIsLoadingChanged(bool value) => OnLoadingChanged();

    private void OnLibraryChanged(object? sender, LibraryChange change)
    {
        if (!ShouldReloadOnLibraryChange(change.Kind)) return;

        _libraryChangeDebouncer.Trigger(async token =>
        {
            _logger.LogDebug("Library changed ({ChangeKind}). Reloading the list.", change.Kind);
            await _dispatcherService.EnqueueAsync(() => token.IsCancellationRequested ? Task.CompletedTask : LoadAsync(token));
        });
    }

    /// <summary>Loads every item. A newer load supersedes one still running, whose result is then dropped.</summary>
    [RelayCommand]
    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        if (_isDisposed) return;
        var version = ++_loadVersion;
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var previous = Interlocked.Exchange(ref _loadCts, cts);
        previous?.Cancel();
        previous?.Dispose();
        var token = cts.Token;
        IsLoading = true;
        HasLoadError = false;

        try
        {
            var items = await LoadItemsAsync(token);
            token.ThrowIfCancellationRequested();
            if (_isDisposed || version != _loadVersion) return;
            AllItems = items.ToList();
            _hasLoaded = true;
            ApplyFilter();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            _logger.LogDebug("List load was canceled.");
        }
        catch (Exception ex)
        {
            if (_isDisposed || version != _loadVersion) return;
            _logger.LogError(ex, "Failed to load the list");
            HasLoadError = true;
            AllItems = [];
            Items.Clear();
        }
        finally
        {
            if (version == _loadVersion) IsLoading = false;
        }
    }

    protected void ApplyFilter()
    {
        var term = SearchTerm;
        var matching = IsSearchActive ? AllItems.Where(item => MatchesSearch(item, term)) : AllItems;
        Items.ReplaceRange(Arrange(matching));
    }

    protected override Task ExecuteSearchAsync(CancellationToken token) =>
        _dispatcherService.EnqueueAsync(() =>
        {
            if (!token.IsCancellationRequested && !_isDisposed) ApplyFilter();
            return Task.CompletedTask;
        });

    private void CancelLoad()
    {
        var cts = Interlocked.Exchange(ref _loadCts, null);
        cts?.Cancel();
        cts?.Dispose();
    }

    public override void ResetState()
    {
        base.ResetState();
        _libraryChangeDebouncer.Cancel();
        CancelLoad();
    }

    public virtual void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        _libraryNotifier.Changed -= OnLibraryChanged;
        ResetState();
        _libraryChangeDebouncer.Dispose();
    }
}
