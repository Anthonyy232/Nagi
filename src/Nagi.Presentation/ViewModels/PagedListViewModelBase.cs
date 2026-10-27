using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;
using Nagi.Presentation.Helpers;
using Nagi.Presentation.Services.Abstractions;

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
    protected readonly ILibraryScanner _libraryScanner;
    protected readonly IUISettingsService _settingsService;

    protected readonly object _loadLock = new();
    private readonly Debouncer _libraryChangeDebouncer = new(TimeSpan.FromSeconds(1));

    private bool _hasLoadedInitialSettings;
    private bool _hasLoadedInitialSortOrder;
    private bool _isSettingSongsPerPage;
    protected bool _pendingReload;
    protected bool _isDisposed;
    private CancellationTokenSource? _pageLoadCts;

    [ObservableProperty] public partial bool IsLoading { get; set; }
    [ObservableProperty] public partial bool HasLoadError { get; set; }
    [ObservableProperty] public partial string TotalItemsText { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNextPage), nameof(HasPreviousPage))]
    public partial int CurrentPage { get; set; } = 1;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNextPage))]
    public partial int TotalPages { get; set; } = 1;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNextPage), nameof(HasPreviousPage))]
    public partial int SongsPerPage { get; set; } = 50;
    [ObservableProperty] public partial int TotalItemCount { get; set; }
    public bool HasNextPage => IsPaginationEnabled && SongsPerPage != 0 && CurrentPage < TotalPages;
    public bool HasPreviousPage => IsPaginationEnabled && SongsPerPage != 0 && CurrentPage > 1;

    /// <summary>
    ///     When false, load all pages in the background and disable page navigation.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNextPage), nameof(HasPreviousPage))]
    public partial bool IsPaginationEnabled { get; set; } = true;

    protected int PageSize => !IsPaginationEnabled || SongsPerPage == 0 ? 500 : SongsPerPage;

    protected PagedListViewModelBase(
        ILibraryReader libraryReader,
        ILibraryScanner libraryScanner,
        IUISettingsService settingsService,
        IDispatcherService dispatcherService,
        ILogger logger)
        : base(dispatcherService, logger)
    {
        _libraryReader = libraryReader;
        _libraryScanner = libraryScanner;
        _settingsService = settingsService;
        _libraryScanner.LibraryContentChanged += OnLibraryContentChanged;
        _settingsService.SongsPerPageChanged += OnSettingsSongsPerPageChanged;
    }

    protected abstract Task<PagedResult<TItem>> LoadPageItemsAsync(int pageNumber, int pageSize, CancellationToken token);

    protected virtual Task LoadInitialSortOrderAsync() => Task.CompletedTask;

    protected abstract string FormatTotalItemsText(int count);

    protected virtual Task OnPageLoadedAsync(PagedResult<TItem> result, CancellationToken token) => Task.CompletedTask;

    /// <summary>
    ///     Hook run in parallel with the page fetch. Default no-op. Subclasses (e.g. song lists
    ///     that need a "Play All" ID list) override this to overlap fetches with the page query.
    /// </summary>
    protected virtual Task OnAuxiliaryLoadAsync(CancellationToken token) => Task.CompletedTask;

    protected virtual void OnLoadingStateChanged(bool isLoading) { }

    protected virtual bool ShouldReloadOnLibraryChange(LibraryChangeType changeType) =>
        changeType != LibraryChangeType.FolderAdded;

    /// <summary>
    ///     Write the page's items into the typed collection. Runs on the UI thread inside
    ///     <see cref="ProcessPagedResult"/>'s single dispatcher hop, alongside pager-state
    ///     assignment, so binding listeners see one coherent transition.
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

        var isPaged = IsPaginationEnabled && SongsPerPage != 0;
        TotalPages = isPaged ? Math.Max(1, result.TotalPages) : 1;
        CurrentPage = isPaged
            ? Math.Clamp(result.PageNumber > 0 ? result.PageNumber : CurrentPage, 1, TotalPages)
            : 1;
        TotalItemCount = result.TotalCount;
        TotalItemsText = FormatTotalItemsText(result.TotalCount);
        ApplyItemsToCollection(result, append);
    }

    partial void OnIsLoadingChanged(bool value) => OnLoadingStateChanged(value);

    private void OnSettingsSongsPerPageChanged(int newSize)
    {
        _dispatcherService.TryEnqueue(() =>
        {
            if (_isDisposed || SongsPerPage == newSize) return;
            _hasLoadedInitialSettings = true;

            CancelInflightPageLoad();

            _isSettingSongsPerPage = true;
            try { SongsPerPage = newSize; }
            finally { _isSettingSongsPerPage = false; }

            CurrentPage = 1;
            _ = RefreshAsync(CancellationToken.None);
        });
    }

    partial void OnSongsPerPageChanged(int value)
    {
        if (_isSettingSongsPerPage) return;
        _hasLoadedInitialSettings = true;

        CancelInflightPageLoad();

        _ = _settingsService.SetSongsPerPageAsync(value);

        _dispatcherService.TryEnqueue(() =>
        {
            CurrentPage = 1;
            _ = RefreshAsync(CancellationToken.None);
        });
    }

    protected virtual void OnLibraryContentChanged(object? sender, LibraryContentChangedEventArgs e)
    {
        if (!ShouldReloadOnLibraryChange(e.ChangeType)) return;

        _libraryChangeDebouncer.Trigger(async token =>
        {
            _logger.LogDebug("Library content changed ({ChangeType}). Refreshing paged list.", e.ChangeType);
            await _dispatcherService.EnqueueAsync(() => token.IsCancellationRequested
                ? Task.CompletedTask : RefreshAsync(token));
        });
    }

    [RelayCommand]
    public async Task NextPageAsync()
    {
        if (_isDisposed || !HasNextPage || IsLoading) return;
        await NavigateToPageAsync(CurrentPage + 1);
    }

    [RelayCommand]
    public async Task PreviousPageAsync()
    {
        if (_isDisposed || !HasPreviousPage || IsLoading) return;
        await NavigateToPageAsync(CurrentPage - 1);
    }

    private async Task NavigateToPageAsync(int requestedPage)
    {
        var previousPage = CurrentPage;
        CurrentPage = requestedPage;
        await ReloadCurrentPageAsync(CancellationToken.None);
        _dispatcherService.TryEnqueue(() =>
        {
            // A failed request leaves the existing items visible. Keep their page number too,
            // unless a newer search, page-size change, or reload has already moved the view.
            if (!_isDisposed && HasLoadError && CurrentPage == requestedPage)
                CurrentPage = previousPage;
        });
    }

    /// <summary>
    ///     Reload the current page after a Next/Previous step. Default delegates to the full
    ///     <see cref="LoadAsync"/>. Subclasses override to drop work that's stable across pages.
    /// </summary>
    protected virtual Task ReloadCurrentPageAsync(CancellationToken cancellationToken) => LoadAsync(cancellationToken);

    /// <summary>
    ///     Reload entry point for external triggers (page-size change, settings push, library
    ///     content change). Defaults to <see cref="LoadAsync"/>. Subclasses with a non-standard
    ///     load pipeline (e.g. <see cref="FolderSongListViewModel"/>'s combined folder+song
    ///     paging) override this to route to their own envelope instead.
    /// </summary>
    protected virtual Task RefreshAsync(CancellationToken cancellationToken) => LoadAsync(cancellationToken);

    /// <summary>
    ///     Hydrates <see cref="SongsPerPage"/> from persisted user settings on first call; no-op
    ///     thereafter. Subclasses with an overridden load pipeline should call this once before
    ///     their first page query so they pick up the user's saved preference.
    /// </summary>
    protected async Task EnsureInitialSettingsLoadedAsync()
    {
        if (_hasLoadedInitialSettings) return;

        var pageSize = await _settingsService.GetSongsPerPageAsync();
        _isSettingSongsPerPage = true;
        try { SongsPerPage = pageSize; }
        finally { _isSettingSongsPerPage = false; }
        _hasLoadedInitialSettings = true;
    }

    /// <summary>
    ///     Loads the current page. Sort/page-size/search/library-change handlers
    ///     reset <see cref="CurrentPage"/> to 1 before invoking.
    /// </summary>
    [RelayCommand]
    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        lock (_loadLock)
        {
            if (_isDisposed) return;
            // If a load is already in flight, queue a follow-up. The finally block
            // re-runs LoadAsync once the current one completes, picking up whatever
            // sort/page-size/search/library state the caller just applied.
            if (IsLoading) { _pendingReload = true; return; }
            IsLoading = true;
        }

        var newCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = newCts.Token;
        var oldCts = Interlocked.Exchange(ref _pageLoadCts, newCts);
        oldCts?.Cancel();
        oldCts?.Dispose();

        HasLoadError = false;

        try
        {
            await EnsureInitialSettingsLoadedAsync();

            if (!_hasLoadedInitialSortOrder)
            {
                await LoadInitialSortOrderAsync();
                _hasLoadedInitialSortOrder = true;
            }

            var pageToLoad = CurrentPage;
            var pageSize = PageSize;

            // SQLite's async APIs still perform native I/O, so keep page loads off the UI thread.
            var pagedResult = await Task.Run(async () =>
            {
                var auxTask = OnAuxiliaryLoadAsync(token);
                var pageTask = LoadValidPageAsync(pageToLoad, pageSize, token);
                await Task.WhenAll(auxTask, pageTask).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();

                return pageTask.Result;
            }, token);

            ProcessPagedResult(pagedResult, token);

            if (token.IsCancellationRequested) return;

            await OnPageLoadedAsync(pagedResult, token);

            if ((!IsPaginationEnabled || SongsPerPage == 0) && pagedResult.HasNextPage)
                _ = LoadRemainingPagesAsync(pagedResult.PageNumber + 1, pageSize, token);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Paged load was canceled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An error occurred while loading a page");
            if (!token.IsCancellationRequested && !_isDisposed) HasLoadError = true;
        }
        finally
        {
            EndLoadAndDrainPendingReload();
        }
    }

    /// <summary>Reloads the last remaining page when deletions move the requested page past the end.</summary>
    protected async Task<PagedResult<TItem>> LoadValidPageAsync(int pageNumber, int pageSize, CancellationToken token)
    {
        var result = await LoadPageItemsAsync(pageNumber, pageSize, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        var totalPages = Math.Max(1, result.TotalPages);
        if (pageNumber > totalPages && result.TotalCount > 0)
        {
            result = await LoadPageItemsAsync(totalPages, pageSize, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
        }
        return result;
    }

    /// <summary>
    ///     Subclasses that run their own load envelope (e.g. <c>LoadPageAsync</c>) must call this
    ///     in their finally block instead of clearing <see cref="IsLoading"/> directly. It coalesces
    ///     any reload that was queued via <see cref="_pendingReload"/> while this load was in flight,
    ///     so a sort/search/page-size change racing against a Next/Previous step isn't dropped.
    /// </summary>
    protected void EndLoadAndDrainPendingReload()
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
            if (rerun && !_isDisposed) _ = RefreshAsync(CancellationToken.None);
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

    protected override void OnSearchTermChangedInternal(string value) => CancelInflightPageLoad();

    protected override async Task ExecuteSearchAsync(CancellationToken token)
    {
        await _dispatcherService.EnqueueAsync(async () =>
        {
            if (token.IsCancellationRequested) return;
            CurrentPage = 1;
            await LoadAsync(token);
        });
    }

    protected void CancelInflightPageLoad()
    {
        var cts = Interlocked.Exchange(ref _pageLoadCts, null);
        cts?.Cancel();
        cts?.Dispose();
    }

    public override void ResetState()
    {
        base.ResetState();
        _libraryChangeDebouncer.Cancel();
        CancelInflightPageLoad();
        lock (_loadLock) _pendingReload = false;
    }

    public virtual void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        CancelPendingSearch();

        _libraryScanner.LibraryContentChanged -= OnLibraryContentChanged;
        _settingsService.SongsPerPageChanged -= OnSettingsSongsPerPageChanged;
        CancelInflightPageLoad();
        _libraryChangeDebouncer.Dispose();
    }
}
