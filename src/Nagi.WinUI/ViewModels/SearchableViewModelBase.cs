using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Nagi.WinUI.Services.Abstractions;

namespace Nagi.WinUI.ViewModels;

/// <summary>
///     Base class for ViewModels that provide debounced search functionality.
/// </summary>
public abstract partial class SearchableViewModelBase : ObservableObject
{
    protected const int DefaultSearchDebounceDelay = 400;
    protected readonly IDispatcherService _dispatcherService;
    protected readonly ILogger _logger;
    private CancellationTokenSource? _debounceCts;
    protected bool _isDisposed;

    protected SearchableViewModelBase(IDispatcherService dispatcherService, ILogger logger)
    {
        _dispatcherService = dispatcherService ?? throw new ArgumentNullException(nameof(dispatcherService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    [ObservableProperty]
    public partial string SearchTerm { get; set; } = string.Empty;

    public bool IsSearchActive => !string.IsNullOrWhiteSpace(SearchTerm);

    /// <summary>
    ///     Gets the delay in milliseconds to wait before triggering a search.
    /// </summary>
    protected virtual int SearchDebounceDelay => DefaultSearchDebounceDelay;

    partial void OnSearchTermChanged(string value)
    {
        if (_isDisposed) return;
        OnSearchTermChangedInternal(value);
        TriggerDebouncedSearch();
    }

    /// <summary>
    ///     Hook for derived classes to perform actions when the search term changes, 
    ///     such as clearing selection.
    /// </summary>
    protected virtual void OnSearchTermChangedInternal(string value)
    {
    }

    /// <summary>
    ///     Executes an immediate search, cancelling any pending debounced search.
    /// </summary>
    [RelayCommand]
    public virtual async Task SearchAsync()
    {
        if (_isDisposed) return;
        CancelPendingSearch();

        _debounceCts = new CancellationTokenSource();
        var token = _debounceCts.Token;

        try
        {
            await ExecuteSearchAsync(token);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Immediate search cancelled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed during immediate search execution.");
        }
    }

    /// <summary>
    ///     Internal triggering logic for debounced search.
    /// </summary>
    protected void TriggerDebouncedSearch()
    {
        if (_isDisposed) return;
        CancelPendingSearch();

        var cts = new CancellationTokenSource();
        Interlocked.Exchange(ref _debounceCts, cts);
        var token = cts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(SearchDebounceDelay, token);

                if (token.IsCancellationRequested || _isDisposed) return;

                await ExecuteSearchAsync(token);
            }
            catch (OperationCanceledException)
            {

            }
            catch (ObjectDisposedException)
            {
                _logger.LogDebug("Search cancelled due to object disposal.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed during debounced search execution.");
            }
        }, token);
    }

    /// <summary>
    ///     Cancels any pending debounced search.
    /// </summary>
    protected void CancelPendingSearch()
    {
        var cts = Interlocked.Exchange(ref _debounceCts, null);
        if (cts != null)
        {
            try
            {
                cts.Cancel();
                cts.Dispose();
            }
            catch (ObjectDisposedException)
            {
                // Ignore if already disposed
            }
        }
    }

    /// <summary>
    ///     Override this method to implement the actual search logic.
    ///     This is called after the debounce delay.
    /// </summary>
    protected abstract Task ExecuteSearchAsync(CancellationToken token);

    /// <summary>
    ///     Cleans up search-related resources.
    /// </summary>
    public virtual void Cleanup()
    {
        _isDisposed = true;
        CancelPendingSearch();
        SearchTerm = string.Empty;
    }
}
