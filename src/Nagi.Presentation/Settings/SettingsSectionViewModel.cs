using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Nagi.Core.Platform;
using Nagi.Core.Settings;
using Nagi.Presentation.Common;

namespace Nagi.Presentation.Settings;

/// <summary>
///     Base for the settings page and its sections. Their setting properties read and write the settings store
///     through <see cref="Get{T}" /> and <see cref="Set{T}" />, so a setting changed anywhere shows on the page.
/// </summary>
public abstract class SettingsSectionViewModel : ObservableObject, IDisposable
{
    // Filled as properties first read their setting; a property nobody has read has nobody to notify.
    private readonly ConcurrentDictionary<Setting, string> _propertyNames = new();
    protected readonly IDispatcherService _dispatcherService;
    protected readonly ILogger _logger;
    protected readonly ISettingsService _settingsService;
    protected bool _isInitializing;
    protected bool _isDisposed;

    protected SettingsSectionViewModel(ISettingsService settingsService, IDispatcherService dispatcherService,
        ILogger logger)
    {
        _settingsService = settingsService;
        _dispatcherService = dispatcherService;
        _logger = logger;
        _settingsService.SettingChanged += HandleSettingChanged;
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        _settingsService.SettingChanged -= HandleSettingChanged;
        OnDisposing();
    }

    /// <summary>
    ///     Releases subscriptions and pending work. Runs once, after the section is marked disposed.
    /// </summary>
    protected virtual void OnDisposing()
    {
    }

    /// <summary>
    ///     Reads <paramref name="setting" /> for the calling property, which then raises PropertyChanged whenever the
    ///     setting changes.
    /// </summary>
    protected T Get<T>(Setting<T> setting, [CallerMemberName] string propertyName = "")
    {
        _propertyNames.TryAdd(setting, propertyName);
        return _settingsService.Get(setting);
    }

    /// <returns>False if the setting couldn't be saved. The failure is logged and the property shows the saved value again.</returns>
    protected bool Set<T>(Setting<T> setting, T value, [CallerMemberName] string propertyName = "")
    {
        try
        {
            _settingsService.Set(setting, value);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save setting {SettingName}", propertyName);
            OnPropertyChanged(propertyName);
            return false;
        }
    }

    /// <summary>
    ///     Called on the UI thread after a setting changed and its property raised PropertyChanged.
    /// </summary>
    protected virtual void OnSettingChanged(Setting setting)
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

    private void HandleSettingChanged(Setting setting)
    {
        if (_dispatcherService.HasThreadAccess) Notify();
        else _dispatcherService.TryEnqueue(Notify);

        void Notify()
        {
            if (_isDisposed) return;
            if (_propertyNames.TryGetValue(setting, out var propertyName)) OnPropertyChanged(propertyName);
            OnSettingChanged(setting);
        }
    }
}
