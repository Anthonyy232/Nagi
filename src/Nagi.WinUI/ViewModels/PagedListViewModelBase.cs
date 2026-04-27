using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;
using Nagi.WinUI.Helpers;
using Nagi.WinUI.Services.Abstractions;

namespace Nagi.WinUI.ViewModels;

/// <summary>
///     Shared plumbing for paged list VMs: settings-backed page size, next/previous
///     commands, library-change debounce, search reset, past-end clamp, and the load
///     envelope. Subclasses own their typed item collection and override
///     <see cref="ApplyItemsToCollection"/> to write into it on the UI thread.
/// </summary>
public abstract partial class PagedListViewModelBase<TItem> : SearchableViewModelBase, IPagedListViewModel, IDisposable
{
    protected readonly ILibraryService _libraryService;
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

    [ObservableProperty] public partial int CurrentPage { get; set; } = 1;
    [ObservableProperty] public partial int TotalPages { get; set; } = 1;
    [ObservableProperty] public partial int SongsPerPage { get; set; } = 50;
    [ObservableProperty] public partial int TotalItemCount { get; set; }
    [ObservableProperty] public partial bool HasNextPage { get; set; }
    [ObservableProperty] public partial bool HasPreviousPage { get; set; }

    /// <summary>
    ///     When true, Next/Previous commands are active and the load envelope drives a single
    ///     page at a time. When false, the subclass is expected to drive an "infinite scroll"
    ///     style load (e.g. via <see cref="OnPageLoadedAsync"/>).
    /// </summary>
    public bool IsPaginationEnabled { get; set; } = true;

    protected PagedListViewModelBase(
        ILibraryService libraryService,
        IUISettingsService settingsService,
        IDispatcherService dispatcherService,
        ILogger logger)
        : base(dispatcherService, logger)
    {
        _libraryService = libraryService;
        _settingsService = settingsService;
        _libraryService.LibraryContentChanged += OnLibraryContentChanged;
        _settingsService.SongsPerPageChanged += OnSettingsSongsPerPageChanged;
    }

    protected abstract Task<PagedResult<TItem>> LoadPageItemsAsync(int pageNumber, int pageSize, CancellationToken token);

    /// <summary>Load the persisted sort order. Default no-op; override if your VM has a sort to hydrate.</summary>
    protected virtual Task LoadInitialSortOrderAsync() => Task.CompletedTask;

    protected abstract string FormatTotalItemsText(int count);

    /// <summary>Hook run after the page has been processed. Default no-op.</summary>
    protected virtual Task OnPageLoadedAsync(PagedResult<TItem> result, CancellationToken token) => Task.CompletedTask;

    /// <summary>
    ///     Hook run in parallel with the page fetch. Default no-op. Subclasses (e.g. song lists
    ///     that need a "Play All" ID list) override this to overlap fetches with the page query.
    /// </summary>
    protected virtual Task OnAuxiliaryLoadAsync(CancellationToken token) => Task.CompletedTask;

    /// <summary>Notifies subclasses of <see cref="IsLoading"/> transitions for command can-execute updates.</summary>
    protected virtual void OnLoadingStateChanged(bool isLoading) { }

    /// <summary>Whether a library content change should trigger a reload. Default: everything except FolderAdded.</summary>
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
        if (result is null || token.IsCancellationRequested || _isDisposed) return;

        _dispatcherService.TryEnqueue(() =>
        {
            if (token.IsCancellationRequested || _isDisposed) return;

            ApplyItemsToCollection(result, append);

            CurrentPage = result.PageNumber > 0 ? result.PageNumber : CurrentPage;
            TotalPages = Math.Max(1, result.TotalPages);
            TotalItemCount = result.TotalCount;
            HasNextPage = result.HasNextPage;
            HasPreviousPage = CurrentPage > 1;
            TotalItemsText = FormatTotalItemsText(result.TotalCount);
        });
    }

    partial void OnIsLoadingChanged(bool value) => OnLoadingStateChanged(value);

    private void OnSettingsSongsPerPageChanged(int newSize)
    {
        _dispatcherService.TryEnqueue(() =>
        {
            if (SongsPerPage == newSize) return;

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

        _isSettingSongsPerPage = true;
        try { _ = _settingsService.SetSongsPerPageAsync(value); }
        finally { _isSettingSongsPerPage = false; }

        _dispatcherService.TryEnqueue(() =>
        {
            CurrentPage = 1;
            _ = RefreshAsync(CancellationToken.None);
        });
    }

    protected virtual void OnLibraryContentChanged(object? sender, LibraryContentChangedEventArgs e)
    {
        if (!ShouldReloadOnLibraryChange(e.ChangeType)) return;

        _libraryChangeDebouncer.Trigger(async _ =>
        {
            _logger.LogDebug("Library content changed ({ChangeType}). Refreshing paged list.", e.ChangeType);
            await _dispatcherService.EnqueueAsync(() => RefreshAsync(CancellationToken.None));
        });
    }

    [RelayCommand]
    public async Task NextPageAsync()
    {
        if (!HasNextPage || IsLoading || !IsPaginationEnabled) return;
        CurrentPage += 1;
        await ReloadCurrentPageAsync(CancellationToken.None);
    }

    [RelayCommand]
    public async Task PreviousPageAsync()
    {
        if (!HasPreviousPage || IsLoading || !IsPaginationEnabled) return;
        CurrentPage -= 1;
        await ReloadCurrentPageAsync(CancellationToken.None);
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

        _isSettingSongsPerPage = true;
        try { SongsPerPage = await _settingsService.GetSongsPerPageAsync(); }
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
            // If a load is already in flight, queue a follow-up. The finally block
            // re-runs LoadAsync once the current one completes, picking up whatever
            // sort/page-size/search/library state the caller just applied.
            if (IsLoading) { _pendingReload = true; return; }
            IsLoading = true;
        }

        var newCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var oldCts = Interlocked.Exchange(ref _pageLoadCts, newCts);
        try { oldCts?.Cancel(); oldCts?.Dispose(); } catch (ObjectDisposedException) { }

        // Dispose may have raced past CancelInflightPageLoad between our swap-in and now.
        // Reclaim the slot if it's still ours and tear down so the new CTS doesn't leak.
        if (_isDisposed)
        {
            var reclaimed = Interlocked.CompareExchange(ref _pageLoadCts, null, newCts);
            if (reclaimed == newCts) { newCts.Cancel(); newCts.Dispose(); }
            EndLoadAndDrainPendingReload();
            return;
        }

        var token = newCts.Token;

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
            var pageSize = SongsPerPage;

            var auxTask = OnAuxiliaryLoadAsync(token);
            var pageTask = LoadPageItemsAsync(pageToLoad, pageSize, token);
            await Task.WhenAll(auxTask, pageTask).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();

            var pagedResult = pageTask.Result;

            // Past-end clamp: if the requested page exceeds the result set (e.g. items removed),
            // jump back to the last valid page and refetch.
            var totalPages = Math.Max(1, pagedResult.TotalPages);
            if (pageToLoad > totalPages && pagedResult.TotalCount > 0)
            {
                pagedResult = await LoadPageItemsAsync(totalPages, pageSize, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
            }

            ProcessPagedResult(pagedResult, token);

            if (token.IsCancellationRequested) return;

            await OnPageLoadedAsync(pagedResult, token);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Paged load was canceled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An error occurred while loading a page");
            HasLoadError = true;
        }
        finally
        {
            EndLoadAndDrainPendingReload();
        }
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

    protected override async Task ExecuteSearchAsync(CancellationToken token)
    {
        await _dispatcherService.EnqueueAsync(async () =>
        {
            if (token.IsCancellationRequested) return;
            CurrentPage = 1;
            await LoadAsync(token);
        });
    }

    /// <summary>Cancels and disposes any in-flight page load. Safe to call from subclasses.</summary>
    protected void CancelInflightPageLoad()
    {
        var cts = Interlocked.Exchange(ref _pageLoadCts, null);
        try { cts?.Cancel(); cts?.Dispose(); } catch (ObjectDisposedException) { }
    }

    public virtual void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        _libraryService.LibraryContentChanged -= OnLibraryContentChanged;
        _settingsService.SongsPerPageChanged -= OnSettingsSongsPerPageChanged;
        CancelInflightPageLoad();
        _libraryChangeDebouncer.Dispose();
    }
}
