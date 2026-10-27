using Windows.UI.ViewManagement;
using Nagi.Core.Services.Abstractions;

namespace Nagi.WinUI.Services.Implementations;

/// <summary>
///     Tracks the Windows "Transparency effects" switch, which decides whether acrylic and mica can be used.
/// </summary>
public sealed class TransparencyEffectsMonitor : IDisposable
{
    private readonly IDispatcherService _dispatcherService;
    private readonly UISettings _uiSettings = new();

    public TransparencyEffectsMonitor(IDispatcherService dispatcherService)
    {
        _dispatcherService = dispatcherService;
        _uiSettings.AdvancedEffectsEnabledChanged += OnAdvancedEffectsEnabledChanged;
    }

    /// <summary>
    ///     Raised on the UI thread with the new value.
    /// </summary>
    public event Action<bool>? Changed;

    public bool IsEnabled => _uiSettings.AdvancedEffectsEnabled;

    public void Dispose() => _uiSettings.AdvancedEffectsEnabledChanged -= OnAdvancedEffectsEnabledChanged;

    private void OnAdvancedEffectsEnabledChanged(UISettings sender, object args) =>
        _dispatcherService.TryEnqueue(() => Changed?.Invoke(_uiSettings.AdvancedEffectsEnabled));
}
