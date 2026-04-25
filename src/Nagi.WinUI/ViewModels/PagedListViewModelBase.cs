using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;
using Nagi.WinUI.Helpers;
using Nagi.WinUI.Services.Abstractions;

namespace Nagi.WinUI.ViewModels;

/// <summary>
///     Shared plumbing for list VMs that use bounded pagination: settings-backed page
///     size, next/previous commands, library-change debounce, and the load envelope.
///     Subclasses own their typed item collection and supply the page fetch + sort
///     hydration + total-items formatting.
/// </summary>
public abstract partial class PagedListViewModelBase : SearchableViewModelBase, IPagedListViewModel, IDisposable
{
    protected readonly ILibraryService _libraryService;
    protected readonly IUISettingsService _settingsService;

    private readonly Debouncer _libraryChangeDebouncer = new(TimeSpan.FromSeconds(1));
    private readonly object _loadLock = new();

    private bool _hasLoadedInitialSettings;
    private bool _hasLoadedInitialSortOrder;
    private bool _isSettingSongsPerPage;
    private bool _isDisposed;
    private CancellationTokenSource? _pageLoadCts;

    [ObservableProperty] public partial bool IsLoading { get; set; }
    [ObservableProperty] public partial bool HasLoadError { get; set; }
    [ObservableProperty] public partial string TotalItemsText { get; set; } = string.Empty;

    [ObservableProperty] public partial int CurrentPage { get; set; } = 1;
    [ObservableProperty] public partial int TotalPages { get; set; } = 1;
    [ObservableProperty] public partial int SongsPerPage { get; set; } = 50;
    [ObservableProperty] public partial bool HasNextPage { get; set; }
    [ObservableProperty] public partial bool HasPreviousPage { get; set; }

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
    }

    /// <summary>
    ///     Fetch the items for a single page and replace the subclass's item collection.
    ///     Return the total number of items across all pages.
    /// </summary>
    protected abstract Task<int> LoadPageItemsAsync(int pageNumber, int pageSize, CancellationToken token);

    /// <summary>Load the persisted sort order. Called once per VM lifetime inside the load envelope.</summary>
    protected abstract Task LoadInitialSortOrderAsync();

    /// <summary>Format the "N items" status text using the subclass's resource strings.</summary>
    protected abstract string FormatTotalItemsText(int count);

    /// <summary>Hook run after each successful page load. Default no-op.</summary>
    protected virtual Task OnPageLoadedAsync() => Task.CompletedTask;

    /// <summary>Whether a library content change should trigger a reload. Default: everything except FolderAdded.</summary>
    protected virtual bool ShouldReloadOnLibraryChange(LibraryChangeType changeType) =>
        changeType != LibraryChangeType.FolderAdded;

    private void OnSettingsSongsPerPageChanged(int newSize)
    {
        _dispatcherService.TryEnqueue(() =>
        {
            if (SongsPerPage == newSize) return;

            _isSettingSongsPerPage = true;
            try { SongsPerPage = newSize; }
            finally { _isSettingSongsPerPage = false; }

            CurrentPage = 1;
            _ = LoadAsync(CancellationToken.None);
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
            _ = LoadAsync(CancellationToken.None);
        });
    }

    private void OnLibraryContentChanged(object? sender, LibraryContentChangedEventArgs e)
    {
        if (!ShouldReloadOnLibraryChange(e.ChangeType)) return;

        _libraryChangeDebouncer.Trigger(async _ =>
        {
            _logger.LogDebug("Library content changed ({ChangeType}). Refreshing paged list.", e.ChangeType);
            await _dispatcherService.EnqueueAsync(() => LoadCommand.ExecuteAsync(CancellationToken.None));
        });
    }

    [RelayCommand]
    public async Task NextPageAsync()
    {
        if (!HasNextPage || IsLoading) return;
        CurrentPage += 1;
        await LoadAsync(CancellationToken.None);
    }

    [RelayCommand]
    public async Task PreviousPageAsync()
    {
        if (!HasPreviousPage || IsLoading) return;
        CurrentPage -= 1;
        await LoadAsync(CancellationToken.None);
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
            if (IsLoading) return;
            IsLoading = true;
        }

        _pageLoadCts?.Cancel();
        _pageLoadCts?.Dispose();
        _pageLoadCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _pageLoadCts.Token;

        HasLoadError = false;

        try
        {
            if (!_hasLoadedInitialSettings)
            {
                _isSettingSongsPerPage = true;
                try { SongsPerPage = await _settingsService.GetSongsPerPageAsync(); }
                finally { _isSettingSongsPerPage = false; }
                _settingsService.SongsPerPageChanged += OnSettingsSongsPerPageChanged;
                _hasLoadedInitialSettings = true;
            }

            if (!_hasLoadedInitialSortOrder)
            {
                await LoadInitialSortOrderAsync();
                _hasLoadedInitialSortOrder = true;
            }

            await LoadPageAsync(CurrentPage, token);

            if (token.IsCancellationRequested) return;

            await OnPageLoadedAsync();
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
            IsLoading = false;
        }
    }

    private async Task LoadPageAsync(int pageToLoad, CancellationToken token)
    {
        var totalCount = await LoadPageItemsAsync(pageToLoad, SongsPerPage, token);
        token.ThrowIfCancellationRequested();

        var totalPages = SongsPerPage > 0 ? Math.Max(1, (int)Math.Ceiling(totalCount / (double)SongsPerPage)) : 1;

        // If the requested page is past the end (e.g. items removed), reload the last valid page.
        if (pageToLoad > totalPages && totalCount > 0)
        {
            CurrentPage = totalPages;
            await LoadPageAsync(totalPages, token);
            return;
        }

        TotalPages = totalPages;
        HasPreviousPage = CurrentPage > 1;
        HasNextPage = CurrentPage < TotalPages;
        TotalItemsText = FormatTotalItemsText(totalCount);
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

    public virtual void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        _libraryService.LibraryContentChanged -= OnLibraryContentChanged;
        _settingsService.SongsPerPageChanged -= OnSettingsSongsPerPageChanged;
        _pageLoadCts?.Cancel();
        _pageLoadCts?.Dispose();
        _libraryChangeDebouncer.Dispose();
    }
}
