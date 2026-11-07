using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Nagi.Core.Library;
using Nagi.Core.Platform;
using Nagi.Core.Settings;
using Nagi.Presentation.Common;
using Nagi.Presentation.Resources;
using Nagi.Presentation.Services;

namespace Nagi.Presentation.ViewModels;

/// <summary>
///     Shared plumbing for paged list VMs: settings-backed page size, next/previous
///     commands, library-change debounce, search reset, past-end clamp, and the load
///     envelope. Subclasses own their typed item collection and override
///     <see cref="ApplyItemsToCollection"/> to write into it on the UI thread.
/// </summary>
public abstract partial class PagedListViewModelBase<TItem> : SearchableViewModelBase, IPagedListViewModel, IDisposable
{
    protected readonly ILibraryReader _libraryReader;
    protected readonly ILibraryNotifier _libraryNotifier;
    protected readonly ISettingsService _settingsService;

    private readonly object _loadLock = new();
    private readonly Debouncer _libraryChangeDebouncer = new(TimeSpan.FromSeconds(1));

    private bool _pendingReload;
    private bool _hasLoaded;
    protected bool _isDisposed;
    private CancellationTokenSource? _loadCts;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmptyState))]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmptyState))]
    public partial bool HasLoadError { get; set; }
    [ObservableProperty] public partial string TotalItemsText { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNextPage), nameof(HasPreviousPage))]
    [NotifyCanExecuteChangedFor(nameof(NextPageCommand), nameof(PreviousPageCommand))]
    public partial int CurrentPage { get; set; } = 1;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNextPage))]
    [NotifyCanExecuteChangedFor(nameof(NextPageCommand))]
    public partial int TotalPages { get; set; } = 1;

    /// <summary>The items a page holds, or 0 to show every item.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNextPage), nameof(HasPreviousPage))]
    [NotifyCanExecuteChangedFor(nameof(NextPageCommand), nameof(PreviousPageCommand))]
    public partial int PageSize { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPaginate), nameof(ShowEmptyState))]
    public partial int TotalItemCount { get; set; }

    /// <summary>Whether a load has finished and found nothing, so the list doesn't look empty while it loads.</summary>
    public bool ShowEmptyState => _hasLoaded && !IsLoading && !HasLoadError && TotalItemCount == 0;

    /// <summary>Paging only means something once there are more items than the smallest page holds.</summary>
    public bool CanPaginate => TotalItemCount > AppSettings.PageSizeOptions[0];

    public bool HasNextPage => IsPaged && CurrentPage < TotalPages;
    public bool HasPreviousPage => IsPaged && CurrentPage > 1;

    /// <summary>Whether the list shows one page at a time. Otherwise it loads every page, one after another.</summary>
    protected virtual bool IsPaged => PageSize != 0;

    private int FetchSize => IsPaged ? PageSize : Paging.MaxPageSize;

    protected PagedListViewModelBase(
        ILibraryReader libraryReader,
        ILibraryNotifier libraryNotifier,
        ISettingsService settingsService,
        IDispatcherService dispatcherService,
        ILogger logger)
        : base(dispatcherService, logger)
    {
        _libraryReader = libraryReader;
        _libraryNotifier = libraryNotifier;
        _settingsService = settingsService;
        PageSize = _settingsService.Get(AppSettings.SongsPerPage);
        _libraryNotifier.Changed += OnLibraryChanged;
        _settingsService.SettingChanged += OnSettingChanged;
    }

    protected abstract Task<PagedResult<TItem>> LoadPageItemsAsync(int pageNumber, int pageSize, CancellationToken token);

    protected abstract string FormatTotalItemsText(PagedResult<TItem> result);

    /// <summary>The header text after a failed load.</summary>
    protected virtual string LoadErrorText => Strings.Generic_Error;

    protected virtual Task OnPageLoadedAsync(PagedResult<TItem> result, CancellationToken token) => Task.CompletedTask;

    /// <summary>Runs beside the page query of every load except a step to the next or previous page.</summary>
    protected virtual Task OnAuxiliaryLoadAsync(CancellationToken token) => Task.CompletedTask;

    protected virtual void OnLoadingStateChanged(bool isLoading) { }

    protected virtual bool ShouldReloadOnLibraryChange(LibraryChangeKind kind) =>
        kind is not (LibraryChangeKind.RootAdded or LibraryChangeKind.FavoritesChanged or LibraryChangeKind.ListenHistoryCleared);

    /// <summary>
    ///     Writes a page's items into the typed collection. Runs on the UI thread in the same dispatcher hop that sets the
    ///     pager, so bindings see one coherent change.
    /// </summary>
    protected virtual void ApplyItemsToCollection(PagedResult<TItem> result, bool append) { }

    /// <summary>
    ///     Async continuations must read continuation values from <paramref name="result"/>,
    ///     not the VM properties — the dispatch hop may not have run yet.
    /// </summary>
    protected void ProcessPagedResult(PagedResult<TItem> result, CancellationToken token, bool append = false)
    {
        if (token.IsCancellationRequested || _isDisposed) return;

        _dispatcherService.TryEnqueue(() => ApplyPagedResult(result, token, append));
    }

    private void ApplyPagedResult(PagedResult<TItem> result, CancellationToken token, bool append)
    {
        if (token.IsCancellationRequested || _isDisposed || _pendingReload) return;

        _hasLoaded = true;
        TotalPages = IsPaged ? Math.Max(1, result.TotalPages) : 1;
        CurrentPage = IsPaged
            ? Math.Clamp(result.PageNumber > 0 ? result.PageNumber : CurrentPage, 1, TotalPages)
            : 1;
        TotalItemCount = result.TotalCount;
        TotalItemsText = FormatTotalItemsText(result);
        ApplyItemsToCollection(result, append);
    }

    partial void OnIsLoadingChanged(bool value) => OnLoadingStateChanged(value);

    partial void OnPageSizeChanged(int value) => _settingsService.Set(AppSettings.SongsPerPage, value);

    // Whichever list changed the page size, every list picks it up from the setting.
    private void OnSettingChanged(Setting setting)
    {
        if (setting != AppSettings.SongsPerPage) return;
        _dispatcherService.TryEnqueue(() =>
        {
            if (_isDisposed) return;
            PageSize = _settingsService.Get(AppSettings.SongsPerPage);
            CancelLoad();
            CurrentPage = 1;
            _ = LoadAsync(CancellationToken.None);
        });
    }

    private void OnLibraryChanged(object? sender, LibraryChange change)
    {
        if (!ShouldReloadOnLibraryChange(change.Kind)) return;

        _libraryChangeDebouncer.Trigger(async token =>
        {
            _logger.LogDebug("Library changed ({ChangeKind}). Refreshing paged list.", change.Kind);
            await _dispatcherService.EnqueueAsync(() => token.IsCancellationRequested
                ? Task.CompletedTask : LoadAsync(token));
        });
    }

    [RelayCommand(CanExecute = nameof(HasNextPage))]
    public async Task NextPageAsync()
    {
        if (_isDisposed || IsLoading) return;
        await StepToPageAsync(CurrentPage + 1);
    }

    [RelayCommand(CanExecute = nameof(HasPreviousPage))]
    public async Task PreviousPageAsync()
    {
        if (_isDisposed || IsLoading) return;
        await StepToPageAsync(CurrentPage - 1);
    }

    private async Task StepToPageAsync(int requestedPage)
    {
        var previousPage = CurrentPage;
        CurrentPage = requestedPage;
        await RunLoadAsync(stepsPage: true, CancellationToken.None);
        _dispatcherService.TryEnqueue(() =>
        {
            // A failed request leaves the existing items visible. Keep their page number too,
            // unless a newer search, page-size change, or reload has already moved the view.
            if (!_isDisposed && HasLoadError && CurrentPage == requestedPage)
                CurrentPage = previousPage;
        });
    }

    /// <summary>
    ///     Loads the current page. A load requested while one runs is queued and runs once it finishes, with whatever
    ///     sort, page size, search or library state the caller has just applied.
    /// </summary>
    [RelayCommand]
    public Task LoadAsync(CancellationToken cancellationToken) => RunLoadAsync(stepsPage: false, cancellationToken);

    private async Task RunLoadAsync(bool stepsPage, CancellationToken cancellationToken)
    {
        lock (_loadLock)
        {
            if (_isDisposed) return;
            if (IsLoading) { _pendingReload = true; return; }
            IsLoading = true;
        }

        var newCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = newCts.Token;
        var oldCts = Interlocked.Exchange(ref _loadCts, newCts);
        oldCts?.Cancel();
        oldCts?.Dispose();

        HasLoadError = false;

        try
        {
            var pageToLoad = CurrentPage;
            var pageSize = FetchSize;

            // SQLite's async APIs still perform native I/O, so keep page loads off the UI thread.
            var pagedResult = await Task.Run(async () =>
            {
                var auxTask = stepsPage ? Task.CompletedTask : OnAuxiliaryLoadAsync(token);
                var pageTask = LoadValidPageAsync(pageToLoad, pageSize, token);
                await Task.WhenAll(auxTask, pageTask).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();

                return pageTask.Result;
            }, token);

            ProcessPagedResult(pagedResult, token);

            if (token.IsCancellationRequested) return;

            await OnPageLoadedAsync(pagedResult, token);

            if (!IsPaged && pagedResult.HasNextPage)
                _ = LoadRemainingPagesAsync(pagedResult.PageNumber + 1, pageSize, token);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Paged load was canceled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An error occurred while loading a page");
            if (!token.IsCancellationRequested && !_isDisposed)
            {
                HasLoadError = true;
                if (!stepsPage) TotalItemsText = LoadErrorText;
            }
        }
        finally
        {
            EndLoadAndDrainPendingReload();
        }
    }

    private async Task<PagedResult<TItem>> LoadValidPageAsync(int pageNumber, int pageSize, CancellationToken token)
    {
        var result = await LoadPageItemsAsync(pageNumber, pageSize, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        // Deletions can leave the requested page past the end; show the last page that remains.
        var totalPages = Math.Max(1, result.TotalPages);
        if (pageNumber > totalPages && result.TotalCount > 0)
        {
            result = await LoadPageItemsAsync(totalPages, pageSize, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
        }
        return result;
    }

    private void EndLoadAndDrainPendingReload()
    {
        _dispatcherService.TryEnqueue(() =>
        {
            bool rerun;
            lock (_loadLock)
            {
                IsLoading = false;
                rerun = _pendingReload;
                _pendingReload = false;
            }
            if (rerun && !_isDisposed) _ = LoadAsync(CancellationToken.None);
        });
    }

    private async Task LoadRemainingPagesAsync(int pageNumber, int pageSize, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var result = await Task.Run(() => LoadPageItemsAsync(pageNumber, pageSize, token), token);
                await _dispatcherService.EnqueueAsync(() =>
                {
                    ApplyPagedResult(result, token, append: true);
                    return Task.CompletedTask;
                }).WaitAsync(token).ConfigureAwait(false);
                if (!result.HasNextPage) break;
                pageNumber++;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load remaining items");
            _dispatcherService.TryEnqueue(() =>
            {
                if (!token.IsCancellationRequested && !_isDisposed) HasLoadError = true;
            });
        }
    }

    protected override void OnSearchTermChangedInternal(string value) => CancelLoad();

    protected override async Task ExecuteSearchAsync(CancellationToken token)
    {
        await _dispatcherService.EnqueueAsync(async () =>
        {
            if (token.IsCancellationRequested) return;
            CurrentPage = 1;
            await LoadAsync(token);
        });
    }

    protected void CancelLoad()
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
        lock (_loadLock) _pendingReload = false;
    }

    public virtual void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        CancelPendingSearch();

        _libraryNotifier.Changed -= OnLibraryChanged;
        _settingsService.SettingChanged -= OnSettingChanged;
        CancelLoad();
        _libraryChangeDebouncer.Dispose();
    }
}
