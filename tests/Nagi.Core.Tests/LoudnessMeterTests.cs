using Nagi.Core.Services.Implementations;
using Xunit;

namespace Nagi.Core.Tests;

/// <summary>
///     Tests for the LoudnessMeter EBU R128 implementation.
///     Test patterns aligned with sdroege/ebur128 and EBU TECH 3341/3342.
///     
///     Key reference values:
///     - A 1kHz stereo sine-wave with peak level at -18 dBFS should measure -18.0 LUFS 
///       (K-weighting gain at 1kHz compensates for the -0.691 offset).
///     - For a 440Hz stereo sine at full scale (amplitude 1.0), the reference library (sdroege/ebur128)
///       produces exactly -0.6500... LUFS for global loudness.
/// </summary>
public class LoudnessMeterTests
{
    private readonly LoudnessMeter _loudnessMeter = new();

    // EBU TECH 3341 compliance tolerance for global loudness
    private const double StrictTolerance = 0.1; // LUFS

    #region Helper Methods

    /// <summary>
    ///     Generates a sine wave with the specified parameters.
    /// </summary>
    private static float[] GenerateSineWave(
        double frequency,
        double amplitude,
        int sampleRate,
        double durationSeconds,
        int channels = 2)
    {
        var totalFrames = (int)(sampleRate * durationSeconds);
        var samples = new float[totalFrames * channels];

        for (var i = 0; i < totalFrames; i++)
        {
            var sampleValue = (float)(amplitude * Math.Sin(2.0 * Math.PI * frequency * i / sampleRate));
            for (var ch = 0; ch < channels; ch++)
            {
                samples[i * channels + ch] = sampleValue;
            }
        }

        return samples;
    }

    /// <summary>
    ///     Generates silence (all zeros).
    /// </summary>
    private static float[] GenerateSilence(int sampleRate, double durationSeconds, int channels = 2)
    {
        var totalFrames = (int)(sampleRate * durationSeconds);
        return new float[totalFrames * channels];
    }

    #endregion

    #region Basic Functionality Tests

    [Fact]
    public void MeasureIntegratedLoudness_EmptyInput_ReturnsNegativeInfinity()
    {
        var result = _loudnessMeter.MeasureIntegratedLoudness([], 48000, 2);
        Assert.True(double.IsNegativeInfinity(result));
    }

    [Fact]
    public void MeasureIntegratedLoudness_Silence_ReturnsNegativeInfinity()
    {
        // Silent audio should be below the -70 LUFS absolute gate
        var samples = GenerateSilence(48000, 2.0);
        var result = _loudnessMeter.MeasureIntegratedLoudness(samples, 48000, 2);
        Assert.True(double.IsNegativeInfinity(result));
    }

    [Fact]
    public void CalculateSamplePeak_FullScale_ReturnsOne()
    {
        var samples = GenerateSineWave(1000, 1.0, 48000, 0.5);
        var result = _loudnessMeter.CalculateSamplePeak(samples);
        Assert.True(result > 0.99 && result <= 1.0);
    }

    #endregion

    #region sdroege/ebur128 Reference Alignment

    /// <summary>
    ///     Matches the `sine_stereo_f32` test in sdroege/ebur128:
    ///     - 440Hz sine wave, stereo identical
    ///     - Full scale (amplitude 1.0)
    ///     - 48kHz sample rate, 5 seconds duration
    ///     - Expected Global Loudness: -0.6500000000000054
    /// </summary>
    [Fact]
    public void MeasureIntegratedLoudness_440Hz_FullScale_Stereo_ReferenceAlignment()
    {
        var samples = GenerateSineWave(440.0, 1.0, 48000, 5.0, channels: 2);
        
        var result = _loudnessMeter.MeasureIntegratedLoudness(samples, 48000, 2);
        const double expected = -0.6500000000000054;

        Assert.True(Math.Abs(result - expected) < StrictTolerance,
            $"Expected reference value {expected:F4} LUFS (±{StrictTolerance}), got {result:F4} LUFS");
    }

    /// <summary>
    ///     Verify that a -18 dBFS peak sine wave (1kHz) measures -18.0 LUFS.
    ///     -18 dBFS peak = 10^(-18/20) amplitude ≈ 0.12589
    ///     Since filter gain at 1kHz is +0.691 dB and formula offset is -0.691 dB,
    ///     Loudness = 20*log10(amplitude) ≈ -18.0 LUFS.
    /// </summary>
    [Fact]
    public void MeasureIntegratedLoudness_Minus18dBFS_1kHz_Stereo_IsConsistent()
    {
        var amplitude = Math.Pow(10, -18.0 / 20.0);
        var samples = GenerateSineWave(1000, amplitude, 48000, 3.0);

        var result = _loudnessMeter.MeasureIntegratedLoudness(samples, 48000, 2);
        const double expected = -17.999; // Effectively -18.0

        Assert.True(Math.Abs(result - expected) < StrictTolerance,
            $"Expected {expected:F2} LUFS (±{StrictTolerance}), got {result:F2} LUFS");
    }

    /// <summary>
    ///     Verify Dual Mono weighting (+6 dB power compared to center channel).
    /// </summary>
    [Fact]
    public void MeasureIntegratedLoudness_DualMono_HasCorrectWeight()
    {
        var monoSamples = GenerateSineWave(1000, 0.5, 48000, 3.0, channels: 1);
        
        // Measure as Center channel (weight 1.0)
        var centerResult = _loudnessMeter.MeasureIntegratedLoudness(monoSamples, 48000, 1);
        
        // Measure as Dual Mono (weight 2.0)
        var dualMonoMap = new[] { LoudnessMeter.Channel.DualMono };
        var dualMonoResult = _loudnessMeter.MeasureIntegratedLoudness(monoSamples, 48000, 1, dualMonoMap);

        // Difference should be exactly 3.01 dB (double the weight, 10*log10(2/1))
        var difference = dualMonoResult - centerResult;
        Assert.True(Math.Abs(difference - 3.01) < 0.01,
            $"Dual Mono should be 3.01 dB louder than Center, got {difference:F2} dB difference");
    }

    #endregion

    #region Sample Rate Consistency

    [Theory]
    [InlineData(44100)]
    [InlineData(48000)]
    [InlineData(96000)]
    public void MeasureIntegratedLoudness_DifferentSampleRates_IsConsistent(int sampleRate)
    {
        // Use -23 LUFS unweighted target for testing
        var amplitude = Math.Pow(10, (-23.0 + 0.691) / 20.0);
        var samples = GenerateSineWave(1000, amplitude, sampleRate, 3.0);

        var result = _loudnessMeter.MeasureIntegratedLoudness(samples, sampleRate, 2);
        
        // At 1kHz, pre-filter boost is ~0.7 dB. So result should be around -22.3 LUFS
        const double expected = -22.3;
        
        Assert.True(Math.Abs(result - expected) < 0.2, // Tighter tolerance for consistency
            $"At {sampleRate} Hz: expected ~{expected:F1} LUFS, got {result:F2} LUFS");
    }

    #endregion

    #region Gating Verification

    /// <summary>
    ///     Verify absolute gate (-70 LUFS).
    /// </summary>
    [Fact]
    public void MeasureIntegratedLoudness_AbsoluteGate_Threshold()
    {
        // Amplitude for -71 LUFS unweighted
        var amplitudeBelow = Math.Pow(10, (-71.0 + 0.69) / 20.0);
        var samplesBelow = GenerateSineWave(1000, amplitudeBelow, 48000, 3.0);
        var resultBelow = _loudnessMeter.MeasureIntegratedLoudness(samplesBelow, 48000, 2);
        Assert.True(double.IsNegativeInfinity(resultBelow));

        // Amplitude for -65 LUFS unweighted
        var amplitudeAbove = Math.Pow(10, (-65.0 + 0.69) / 20.0);
        var samplesAbove = GenerateSineWave(1000, amplitudeAbove, 48000, 3.0);
        var resultAbove = _loudnessMeter.MeasureIntegratedLoudness(samplesAbove, 48000, 2);
        Assert.False(double.IsNegativeInfinity(resultAbove));
    }

    #endregion
}
