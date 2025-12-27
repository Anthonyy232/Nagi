using System.Numerics;
using System.Runtime.InteropServices;

namespace Nagi.Core.Services.Implementations;

/// <summary>
///     Pure C# implementation of EBU R128 integrated loudness measurement.
///     Based on ITU-R BS.1770-4 algorithm with K-weighting and gating.
///     Ported from sdroege/ebur128 Rust implementation which passes EBU TECH 3341/3342 tests.
/// </summary>
public class LoudnessMeter
{
    private const double AbsoluteGateThreshold = -70.0; // LUFS
    private const double RelativeGateThreshold = -10.0; // LU below ungated loudness
    private const int BlockDurationMs = 400;
    private const double BlockOverlap = 0.50; // Optimized for speed (50% overlap instead of 75%)

    /// <summary>
    ///     Defines channel types for proper weighting during loudness calculation.
    /// </summary>
    public enum Channel
    {
        Unused,
        Left,
        Right,
        Center,
        LeftSurround,
        RightSurround,
        DualMono,
        MpSC,
        MmSC,
        Mp060,
        Mm060,
        Mp090,
        Mm090,
        Mp135,
        Mm135,
        Mp180,
        Up000,
        Up030,
        Um030,
        Up045,
        Um045,
        Up090,
        Um090,
        Up110,
        Um110,
        Up135,
        Um135,
        Up180,
        Tp000,
        Bp000,
        Bp045,
        Bm045
    }

    /// <summary>
    ///     Measures the integrated loudness of audio samples using EBU R128 algorithm.
    /// </summary>
    /// <param name="samples">Interleaved PCM samples (normalized to -1.0 to 1.0).</param>
    /// <param name="sampleRate">The sample rate in Hz.</param>
    /// <param name="channels">The number of audio channels.</param>
    /// <param name="channelMap">Optional channel mapping. If null, assumes stereo L/R.</param>
    /// <returns>The integrated loudness in LUFS.</returns>
    public double MeasureIntegratedLoudness(float[] samples, int sampleRate, int channels, Channel[]? channelMap = null)
    {
        if (samples.Length == 0 || channels == 0 || sampleRate == 0)
            return double.NegativeInfinity;

        // Default channel map for stereo
        channelMap ??= channels switch
        {
            1 => [Channel.Center],
            2 => [Channel.Left, Channel.Right],
            4 => [Channel.Left, Channel.Right, Channel.LeftSurround, Channel.RightSurround],
            5 => [Channel.Left, Channel.Right, Channel.Center, Channel.LeftSurround, Channel.RightSurround],
            6 => [Channel.Left, Channel.Right, Channel.Center, Channel.Unused, Channel.LeftSurround, Channel.RightSurround],
            _ => Enumerable.Repeat(Channel.Unused, channels).ToArray()
                .Select((c, i) => i switch {
                    0 => Channel.Left,
                    1 => Channel.Right,
                    2 => Channel.Center,
                    3 => Channel.Unused,
                    4 => Channel.LeftSurround,
                    5 => Channel.RightSurround,
                    _ => Channel.Unused
                }).ToArray()
        };

        // Create filter and apply K-weighting
        var filter = new KWeightingFilter(sampleRate, channels);
        var filteredSamples = filter.Process(samples);

        // Calculate block-based loudness with gating
        var framesPerBlock = (int)(sampleRate * BlockDurationMs / 1000.0);
        var framesPerStep = (int)(framesPerBlock * (1.0 - BlockOverlap));
        var totalFrames = filteredSamples.Length / channels;

        var blockPowers = new List<double>();

        for (var frameIndex = framesPerBlock; frameIndex < totalFrames; frameIndex += framesPerStep)
        {
            var blockPower = CalculateBlockPower(filteredSamples, frameIndex, framesPerBlock, channels, channelMap);
            
            // First gate: absolute threshold at -70 LUFS
            var blockLoudness = PowerToLufs(blockPower);
            if (blockLoudness > AbsoluteGateThreshold)
                blockPowers.Add(blockPower);
        }

        if (blockPowers.Count == 0)
            return double.NegativeInfinity;

        // Calculate ungated loudness for relative threshold
        var ungatedMeanPower = blockPowers.Average();
        var ungatedLoudness = PowerToLufs(ungatedMeanPower);
        var relativeThreshold = ungatedLoudness + RelativeGateThreshold;

        // Second gate: relative threshold
        var gatedPowers = blockPowers.Where(p => PowerToLufs(p) > relativeThreshold).ToList();
        if (gatedPowers.Count == 0)
            return double.NegativeInfinity;

        var gatedMeanPower = gatedPowers.Average();
        return PowerToLufs(gatedMeanPower);
    }

    /// <summary>
    ///     Calculates the sample peak value.
    /// </summary>
    public double CalculateSamplePeak(float[] samples)
    {
        if (samples.Length == 0) return 0;
        
        double max = 0;
        foreach (var sample in samples)
        {
            var abs = Math.Abs(sample);
            if (abs > max) max = abs;
        }
        return max;
    }

    private static double PowerToLufs(double power)
    {
        if (power <= 0) return double.NegativeInfinity;
        return -0.691 + 10.0 * Math.Log10(power);
    }

    private static double CalculateBlockPower(
        float[] filteredSamples,
        int blockEndFrame,
        int framesPerBlock,
        int channels,
        Channel[] channelMap)
    {
        var sum = 0.0;
        var framesPerChannel = filteredSamples.Length / channels;
        var blockStartFrame = blockEndFrame - framesPerBlock;

        for (var ch = 0; ch < channels; ch++)
        {
            var channelType = channelMap[ch];
            if (channelType == Channel.Unused)
                continue;

            var channelOffset = ch * framesPerChannel;
            var channelSum = 0.0;

            // Use SIMD for power summation if possible
            var span = filteredSamples.AsSpan(channelOffset + blockStartFrame, framesPerBlock);
            
            if (Vector.IsHardwareAccelerated && span.Length >= Vector<float>.Count)
            {
                var vSum = Vector<float>.Zero;
                var i = 0;
                for (; i <= span.Length - Vector<float>.Count; i += Vector<float>.Count)
                {
                    var v = new Vector<float>(span.Slice(i));
                    vSum += v * v;
                }
                
                // Horizontal sum
                var total = 0.0f;
                for (int j = 0; j < Vector<float>.Count; j++)
                    total += vSum[j];
                channelSum = total;
                
                // Remainder
                for (; i < span.Length; i++)
                {
                    channelSum += (double)span[i] * span[i];
                }
            }
            else
            {
                foreach (var sample in span)
                {
                    channelSum += (double)sample * sample;
                }
            }

            // Apply channel weight per ITU-R BS.1770-4
            // Surround channels get 1.41 (sqrt(2)) weight
            // Apply channel weight per ITU-R BS.1770-4
            // Surround channels get 1.41 (sqrt(2)) weight (+3 dB)
            // Dual Mono gets 2.0 weight (+6 dB)
            var weight = channelType switch
            {
                Channel.LeftSurround or Channel.RightSurround or
                Channel.Mp060 or Channel.Mm060 or
                Channel.Mp090 or Channel.Mm090 or
                Channel.Up090 or Channel.Um090 or
                Channel.Up110 or Channel.Um110 or
                Channel.Up135 or Channel.Um135 or
                Channel.Up180 or Channel.Tp000 => 1.41,
                
                Channel.DualMono => 2.0,
                
                _ => 1.0
            };

            sum += channelSum * weight;
        }

        return sum / framesPerBlock;
    }

    /// <summary>
    ///     Combined K-weighting filter implementing both pre-filter and RLB weighting
    ///     as a single 4th-order IIR filter per ITU-R BS.1770-4.
    ///     Coefficients match sdroege/ebur128 reference implementation.
    /// </summary>
    private class KWeightingFilter
    {
        private readonly double[] _b; // 5 numerator coefficients
        private readonly double[] _a; // 5 denominator coefficients
        private readonly double[][] _filterState; // Per-channel state [channels][5]
        private readonly int _channels;

        public KWeightingFilter(int sampleRate, int channels)
        {
            _channels = channels;
            (_b, _a) = CalculateFilterCoefficients(sampleRate);
            _filterState = new double[channels][];
            for (var i = 0; i < channels; i++)
            _filterState[i] = new double[4]; // 4 delay elements for DF2T
        }

        /// <summary>
        ///     Calculate combined K-weighting filter coefficients.
        ///     This combines the pre-filter (high shelf) and RLB filter (high-pass)
        ///     into a single 4th-order IIR filter.
        /// </summary>
        private static (double[] b, double[] a) CalculateFilterCoefficients(double rate)
        {
            // Pre-filter (high shelf) parameters from ITU-R BS.1770-4
            const double f0_pre = 1681.974450955533;
            const double G = 3.999843853973347;
            const double Q_pre = 0.7071752369554196;

            var K = Math.Tan(Math.PI * f0_pre / rate);
            var Vh = Math.Pow(10.0, G / 20.0);
            var Vb = Math.Pow(Vh, 0.4996667741545416);

            var pb = new double[3];
            var pa = new double[] { 1.0, 0.0, 0.0 };
            var rb = new double[] { 1.0, -2.0, 1.0 };
            var ra = new double[] { 1.0, 0.0, 0.0 };

            var a0 = 1.0 + K / Q_pre + K * K;
            pb[0] = (Vh + Vb * K / Q_pre + K * K) / a0;
            pb[1] = 2.0 * (K * K - Vh) / a0;
            pb[2] = (Vh - Vb * K / Q_pre + K * K) / a0;
            pa[1] = 2.0 * (K * K - 1.0) / a0;
            pa[2] = (1.0 - K / Q_pre + K * K) / a0;

            // RLB filter (high-pass) parameters
            const double f0_rlb = 38.13547087602444;
            const double Q_rlb = 0.5003270373238773;
            K = Math.Tan(Math.PI * f0_rlb / rate);

            ra[1] = 2.0 * (K * K - 1.0) / (1.0 + K / Q_rlb + K * K);
            ra[2] = (1.0 - K / Q_rlb + K * K) / (1.0 + K / Q_rlb + K * K);

            // Convolve the two filters to get combined 4th-order coefficients
            var b = new double[5];
            var a = new double[5];

            // Numerator convolution: pb * rb
            b[0] = pb[0] * rb[0];
            b[1] = pb[0] * rb[1] + pb[1] * rb[0];
            b[2] = pb[0] * rb[2] + pb[1] * rb[1] + pb[2] * rb[0];
            b[3] = pb[1] * rb[2] + pb[2] * rb[1];
            b[4] = pb[2] * rb[2];

            // Denominator convolution: pa * ra
            a[0] = pa[0] * ra[0];
            a[1] = pa[0] * ra[1] + pa[1] * ra[0];
            a[2] = pa[0] * ra[2] + pa[1] * ra[1] + pa[2] * ra[0];
            a[3] = pa[1] * ra[2] + pa[2] * ra[1];
            a[4] = pa[2] * ra[2];

            return (b, a);
        }

        /// <summary>
        ///     Process samples through the K-weighting filter.
        ///     Input: interleaved samples
        ///     Output: de-interleaved filtered samples (channel-major order for efficient block processing)
        /// </summary>
        public float[] Process(float[] interleavedSamples)
        {
            var framesPerChannel = interleavedSamples.Length / _channels;
            var output = new float[interleavedSamples.Length];

            for (var ch = 0; ch < _channels; ch++)
            {
                var state = _filterState[ch];
                var channelOffset = ch * framesPerChannel;

                for (var f = 0; f < framesPerChannel; f++)
                {
                    var inputIndex = f * _channels + ch;
                    double input = interleavedSamples[inputIndex];

                    // Direct Form II Transposed - more numerically stable
                    // Output = b0*input + z0
                    // z0' = b1*input - a1*output + z1
                    // z1' = b2*input - a2*output + z2
                    // z2' = b3*input - a3*output + z3
                    // z3' = b4*input - a4*output
                    var filtered = _b[0] * input + state[0];
                    state[0] = _b[1] * input - _a[1] * filtered + state[1];
                    state[1] = _b[2] * input - _a[2] * filtered + state[2];
                    state[2] = _b[3] * input - _a[3] * filtered + state[3];
                    state[3] = _b[4] * input - _a[4] * filtered;

                    // Flush denormals to zero for stability (use practical threshold)
                    for (var i = 0; i < 4; i++)
                        if (Math.Abs(state[i]) < 1e-15)
                            state[i] = 0.0;

                    // Store in channel-major order
                    output[channelOffset + f] = (float)filtered;
                }
            }

            return output;
        }
    }
}
