namespace Nagi.Core.Models;

public class EqualizerSettings
{
    /// <summary>
    ///     Nagi's default LibVLC equalizer preamp in decibels. LibVLC's nominal flat/unity
    ///     preset is approximately 12 dB; using 10 dB intentionally leaves about 2 dB of
    ///     headroom for modest equalizer boosts.
    /// </summary>
    public const float DefaultPreampDb = 10.0f;

    /// <summary>The most a band or the preamp can boost, in decibels. The most it can cut is the negative of this.</summary>
    public const float MaxGainDb = 20.0f;

    public const float MinGainDb = -MaxGainDb;

    public float Preamp { get; set; } = DefaultPreampDb;

    public List<float> BandGains { get; set; } = new();
}
