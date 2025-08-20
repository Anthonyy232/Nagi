namespace Nagi.Core.Models;

/// <summary>
///     Represents the persisted state of the audio equalizer.
/// </summary>
public class EqualizerSettings
{
    /// <summary>
    ///     The pre-amplification level in decibels.
    /// </summary>
    public float Preamp { get; set; }

    /// <summary>
    ///     A list of amplification values (gains) for each equalizer band.
    /// </summary>
    public List<float> BandGains { get; set; } = new();
}