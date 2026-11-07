using Nagi.Core.Models;

namespace Nagi.Core.Services.Abstractions;

/// <summary>
///     The audio player's equalizer: its bands, the saved gains and preamp, and the built-in presets. Every change is
///     applied to the player and saved.
/// </summary>
public interface IEqualizerService
{
    /// <summary>Empty until <see cref="InitializeAsync" /> completes, and when the audio player has no equalizer.</summary>
    IReadOnlyList<(uint Index, float Frequency)> Bands { get; }

    /// <summary>Null until <see cref="InitializeAsync" /> completes, and when the audio player has no equalizer.</summary>
    EqualizerSettings? CurrentSettings { get; }

    IReadOnlyList<EqualizerPreset> Presets { get; }

    event Action? SettingsChanged;

    /// <summary>
    ///     Reads the audio player's bands and applies the saved settings to it. Every caller gets the same task, so
    ///     anything that needs the equalizer awaits this instead of depending on start-up order. Never throws.
    /// </summary>
    Task InitializeAsync();

    /// <param name="gain">
    ///     In decibels, from <see cref="EqualizerSettings.MinGainDb" /> to <see cref="EqualizerSettings.MaxGainDb" />.
    /// </param>
    Task SetBandGainAsync(uint bandIndex, float gain);

    /// <summary>Sets the bands in order; bands beyond <paramref name="gains" /> keep their gain.</summary>
    Task SetBandGainsAsync(IEnumerable<float> gains);

    /// <inheritdoc cref="SetBandGainAsync" />
    Task SetPreampAsync(float gain);

    /// <summary>Flattens every band and restores the default preamp.</summary>
    Task ResetAsync();

    /// <returns>The preset whose gains match, or null.</returns>
    EqualizerPreset? GetMatchingPreset(IEnumerable<float> bandGains);
}
