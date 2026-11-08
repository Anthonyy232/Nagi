using Nagi.Core.Settings;

namespace Nagi.Core.OnlineProviders;

/// <summary>
///     Lets an online metadata lookup run only while online metadata is on and an enabled source can answer it, and
///     stops the lookups in flight when the user turns online metadata off.
/// </summary>
internal sealed class OnlineLookupGate : IDisposable
{
    private readonly OnlineLookupCancellation _onlineLookups = new();
    private readonly ISettingsService _settingsService;
    private readonly ProviderCapabilities? _capabilities;

    /// <param name="capabilities">
    ///     What an enabled source must be able to do for the lookup to run, or null to run with any enabled source.
    /// </param>
    public OnlineLookupGate(ISettingsService settingsService, ProviderCapabilities? capabilities)
    {
        _settingsService = settingsService;
        _capabilities = capabilities;
        _settingsService.SettingChanged += OnSettingChanged;
    }

    /// <param name="lookup">Runs with every enabled source, in the user's order.</param>
    /// <returns>
    ///     The lookup's result, or null when online metadata is off, no enabled source can answer, or online metadata was
    ///     turned off while it ran.
    /// </returns>
    public async Task<T?> RunAsync<T>(Func<IReadOnlyList<ServiceProviderSetting>, CancellationToken, Task<T>> lookup,
        CancellationToken cancellationToken) where T : class
    {
        var turnedOff = _onlineLookups.Token;
        if (!_settingsService.Get(CoreSettings.FetchOnlineMetadata)) return null;
        var enabledProviders = _settingsService.Get(CoreSettings.MetadataProviders).Where(p => p.IsEnabled).ToList();
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
        _onlineLookups.CancelRunning();
    }

    private void OnSettingChanged(Setting setting)
    {
        if (setting == CoreSettings.FetchOnlineMetadata && !_settingsService.Get(CoreSettings.FetchOnlineMetadata))
            _onlineLookups.CancelRunning();
    }
}
