namespace Nagi.Core.Models;

/// <summary>
///     Represents the persisted state of the audio equalizer.
/// </summary>
public class EqualizerSettings
{
    /// <summary>
    ///     Nagi's default LibVLC equalizer preamp in decibels. LibVLC's nominal flat/unity
    ///     preset is approximately 12 dB; using 10 dB intentionally leaves about 2 dB of
    ///     headroom for modest equalizer boosts.
    /// </summary>
    public const float DefaultPreampDb = 10.0f;

    /// <summary>
    ///     The pre-amplification level in decibels.
    /// </summary>
    public float Preamp { get; set; } = DefaultPreampDb;

    /// <summary>
    ///     A list of amplification values (gains) for each equalizer band.
    /// </summary>
    public List<float> BandGains { get; set; } = new();
}
