using Nagi.Core.Settings;

namespace Nagi.Core.OnlineProviders;

/// <summary>
///     Lets an online lookup run only while its setting is on and an enabled source can answer it, and stops the
///     lookups in flight when the user turns the setting off.
/// </summary>
internal sealed class OnlineLookupGate : IDisposable
{
    private readonly object _lock = new();
    private readonly ISettingsService _settingsService;
    private readonly Setting<bool> _enabled;
    private readonly Setting<List<ServiceProviderSetting>> _providers;
    private readonly ProviderCapabilities? _capabilities;
    private CancellationTokenSource _running = new();

    /// <param name="enabled">The setting that turns the lookups on.</param>
    /// <param name="providers">The setting that lists the lookups' sources in the user's order.</param>
    /// <param name="capabilities">
    ///     What an enabled source must be able to do for the lookup to run, or null to run with any enabled source.
    /// </param>
    public OnlineLookupGate(ISettingsService settingsService, Setting<bool> enabled,
        Setting<List<ServiceProviderSetting>> providers, ProviderCapabilities? capabilities = null)
    {
        _settingsService = settingsService;
        _enabled = enabled;
        _providers = providers;
        _capabilities = capabilities;
        _settingsService.SettingChanged += OnSettingChanged;
    }

    /// <param name="lookup">Runs with every enabled source, in the user's order.</param>
    /// <returns>
    ///     The lookup's result, or null when the setting is off, no enabled source can answer, or the setting was
    ///     turned off while it ran.
    /// </returns>
    public async Task<T?> RunAsync<T>(Func<IReadOnlyList<ServiceProviderSetting>, CancellationToken, Task<T>> lookup,
        CancellationToken cancellationToken) where T : class?
    {
        // Taken before the setting is read, so a lookup either sees the setting off or is cancelled.
        CancellationToken turnedOff;
        lock (_lock) turnedOff = _running.Token;
        if (!_settingsService.Get(_enabled)) return null;
        var enabledProviders = _settingsService.Get(_providers).Where(p => p.IsEnabled).ToList();
        if (!enabledProviders.Any(p => _capabilities is not { } needed || ProviderCatalog.Can(p.Id, needed))) return null;

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, turnedOff);
        try
        {
            return await lookup(enabledProviders, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    public void Dispose()
    {
        _settingsService.SettingChanged -= OnSettingChanged;
        CancelRunning();
    }

    private void OnSettingChanged(Setting setting)
    {
        if (setting == _enabled && !_settingsService.Get(_enabled)) CancelRunning();
    }

    private void CancelRunning()
    {
        CancellationTokenSource running;
        lock (_lock)
        {
            running = _running;
            _running = new CancellationTokenSource();
        }

        running.Cancel();
    }
}
