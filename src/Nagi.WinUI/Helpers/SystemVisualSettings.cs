using Microsoft.UI.Dispatching;
using Windows.UI.ViewManagement;

namespace Nagi.WinUI.Helpers;

/// <summary>
///     The Windows animation, transparency and high contrast switches. The change events are raised on the UI thread
///     with the new value.
/// </summary>
internal static class SystemVisualSettings
{
    private static readonly UISettings Settings = new();
    private static readonly AccessibilitySettings Accessibility = new();

    // Every reader and subscriber runs on the UI thread, so the first use captures its dispatcher.
    private static readonly DispatcherQueue? UIDispatcher = DispatcherQueue.GetForCurrentThread();

    private static bool _lastHighContrast = Accessibility.HighContrast;

    static SystemVisualSettings()
    {
        Settings.AdvancedEffectsEnabledChanged += (_, _) =>
            Raise(() => TransparencyEffectsEnabledChanged?.Invoke(TransparencyEffectsEnabled));
        Settings.ColorValuesChanged += (_, _) => Raise(() =>
        {
            var highContrast = HighContrast;
            if (highContrast == _lastHighContrast) return;
            _lastHighContrast = highContrast;
            HighContrastChanged?.Invoke(highContrast);
        });
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
            Settings.AnimationsEnabledChanged += (_, _) =>
                Raise(() => AnimationsEnabledChanged?.Invoke(AnimationsEnabled));
    }

    public static bool AnimationsEnabled => Settings.AnimationsEnabled;

    /// <summary>
    ///     Whether acrylic, mica and other blur effects may be used.
    /// </summary>
    public static bool TransparencyEffectsEnabled => Settings.AdvancedEffectsEnabled;

    public static bool HighContrast => Accessibility.HighContrast;

    public static event Action<bool>? AnimationsEnabledChanged;

    public static event Action<bool>? TransparencyEffectsEnabledChanged;

    public static event Action<bool>? HighContrastChanged;

    private static void Raise(DispatcherQueueHandler raise) => UIDispatcher?.TryEnqueue(raise);
}
