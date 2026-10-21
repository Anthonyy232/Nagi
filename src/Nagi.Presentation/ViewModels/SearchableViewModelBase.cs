using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Nagi.Core.Services.Abstractions;
using Nagi.Presentation.Helpers;

namespace Nagi.Presentation.ViewModels;

/// <summary>
///     Base class for ViewModels that provide debounced search functionality.
/// </summary>
public abstract partial class SearchableViewModelBase : ObservableObject
{
    protected readonly IDispatcherService _dispatcherService;
    protected readonly ILogger _logger;
    private readonly Debouncer _searchDebouncer = new(TimeSpan.FromMilliseconds(300));

    protected SearchableViewModelBase(IDispatcherService dispatcherService, ILogger logger)
    {
        _dispatcherService = dispatcherService ?? throw new ArgumentNullException(nameof(dispatcherService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSearchActive))]
    public partial string SearchTerm { get; set; } = string.Empty;

    public bool IsSearchActive => !string.IsNullOrWhiteSpace(SearchTerm);

    partial void OnSearchTermChanged(string value)
    {
        OnSearchTermChangedInternal(value);
        _searchDebouncer.Trigger(ExecuteSearchWithErrorHandlingAsync);
    }

    protected virtual void OnSearchTermChangedInternal(string value)
    {
    }

    /// <summary>
    ///     Executes an immediate search, cancelling any pending debounced search.
    /// </summary>
    [RelayCommand]
    public virtual Task SearchAsync() => _searchDebouncer.RunAsync(ExecuteSearchWithErrorHandlingAsync, immediate: true);

    private async Task ExecuteSearchWithErrorHandlingAsync(CancellationToken token)
    {
        try
        {
            await ExecuteSearchAsync(token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Search failed.");
        }
    }

    protected void CancelPendingSearch() => _searchDebouncer.Cancel();

    protected abstract Task ExecuteSearchAsync(CancellationToken token);

    /// <summary>
    ///     Cancels pending operations and resets transient state.
    ///     For Singletons, this should be called when navigating away, but it must NOT dispose the object.
    /// </summary>
    public virtual void ResetState()
    {
        CancelPendingSearch();
    }
}
