using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;
using Nagi.Presentation.Helpers;

namespace Nagi.Presentation.ViewModels;

/// <summary>
///     Shared persistence plumbing for the settings page and its sections: suppressing saves while
///     values are loaded, saving immediately, and debouncing saves for rapidly changing values.
/// </summary>
public abstract class SettingsSectionViewModel : ObservableObject, IDisposable
{
    protected readonly IDispatcherService _dispatcherService;
    protected readonly ILogger _logger;
    protected bool _isInitializing;
    protected bool _isDisposed;

    protected SettingsSectionViewModel(IDispatcherService dispatcherService, ILogger logger)
    {
        _dispatcherService = dispatcherService;
        _logger = logger;
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        OnDisposing();
    }

    /// <summary>
    ///     Releases subscriptions and pending work. Runs once, after the section is marked disposed.
    /// </summary>
    protected virtual void OnDisposing()
    {
    }

    protected void QueueSettingsSave(Debouncer debouncer, Func<CancellationToken, Task> save, string settingName)
    {
        if (_isDisposed) return;
        debouncer.Trigger(async token =>
        {
            try
            {
                await _dispatcherService.EnqueueAsync(() => token.IsCancellationRequested || _isDisposed
                    ? Task.CompletedTask : save(token));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Failed to save setting {SettingName}", settingName);
            }
        });
    }

    protected void ApplyWithoutSaving(Action update)
    {
        var wasInitializing = _isInitializing;
        _isInitializing = true;
        try { update(); }
        finally { _isInitializing = wasInitializing; }
    }

    protected async Task SaveSettingAsync(Func<Task> save, [CallerMemberName] string? settingName = null)
    {
        if (_isInitializing || _isDisposed) return;
        try
        {
            await save();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save setting {SettingName}", settingName);
        }
    }

    /// <summary>
    ///     Reports a library-wide scan on the global operation bar until <paramref name="token" />
    ///     is cancelled or this section is disposed.
    /// </summary>
    protected IProgress<ScanProgress> CreateGlobalOperationProgress(GlobalOperationViewModel operation,
        CancellationToken token) =>
        new Progress<ScanProgress>(progress =>
        {
            if (token.IsCancellationRequested || _isDisposed) return;
            operation.StatusMessage = progress.StatusText;
            operation.IsIndeterminate = progress.IsIndeterminate || progress.Percentage < 5;
            operation.ProgressValue = progress.Percentage;
        });
}
