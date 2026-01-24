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
        CancelPendingSearch();

        var cts = new CancellationTokenSource();
        Interlocked.Exchange(ref _debounceCts, cts);
        var token = cts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(SearchDebounceDelay, token);

                if (token.IsCancellationRequested) return;

                await ExecuteSearchAsync(token);
            }
            catch (OperationCanceledException)
            {

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
    ///     Cancels pending operations and resets transient state.
    ///     For Singletons, this should be called when navigating away, but it must NOT dispose the object.
    /// </summary>
    public virtual void ResetState()
    {
        CancelPendingSearch();
        // Option: SearchTerm = string.Empty; // Decided to keep search term for better UX when navigating back
    }
}
