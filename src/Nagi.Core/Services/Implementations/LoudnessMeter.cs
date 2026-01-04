using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using Nagi.Core.Services.Abstractions;

namespace Nagi.Core.Services.Implementations;

/// <summary>
///     Pure C# implementation of EBU R128 integrated loudness measurement.
///     Optimized for extreme performance with SIMD-accelerated peak detection,
///     O(1) sliding window buffers, zero-copy memory patterns, and ArrayPool allocation.
/// </summary>
public class LoudnessMeter
{
    private const double AbsoluteGateThreshold = -70.0; // LUFS
    private const double RelativeGateThreshold = -10.0; // LU below ungated loudness
    private const int BlockDurationMs = 400;
    private const double BlockOverlap = 0.50;

    public enum Channel
    {
        Unused, Left, Right, Center, LeftSurround, RightSurround, DualMono,
        MpSC, MmSC, Mp060, Mm060, Mp090, Mm090, Mp135, Mm135, Mp180,
        Up000, Up030, Um030, Up045, Um045, Up090, Um090, Up110, Um110,
        Up135, Um135, Up180, Tp000, Bp000, Bp045, Bm045
    }

    public Session CreateSession(int sampleRate, int channels, Channel[]? channelMap = null)
    {
        return new Session(sampleRate, channels, channelMap);
    }

    public double MeasureIntegratedLoudness(float[] samples, int sampleRate, int channels, Channel[]? channelMap = null)
    {
        if (samples.Length == 0 || channels == 0 || sampleRate == 0)
            return double.NegativeInfinity;

        using var session = CreateSession(sampleRate, channels, channelMap);
        session.ProcessChunk(new AudioChunk(samples, sampleRate, channels));
        return session.GetIntegratedLoudness();
    }

    public double CalculateSamplePeak(float[] samples)
    {
        if (samples.Length == 0) return 0;
        return CalculatePeakSimd(samples);
    }

    private static float CalculatePeakSimd(ReadOnlySpan<float> samples)
    {
        float max = 0;
        int i = 0;
        if (Vector.IsHardwareAccelerated && samples.Length >= Vector<float>.Count)
        {
            var vMax = Vector<float>.Zero;
            for (; i <= samples.Length - Vector<float>.Count; i += Vector<float>.Count)
            {
                var v = new Vector<float>(samples.Slice(i));
                vMax = Vector.Max(vMax, Vector.Abs(v));
            }
            for (int j = 0; j < Vector<float>.Count; j++)
                if (vMax[j] > max) max = vMax[j];
        }
        for (; i < samples.Length; i++)
        {
            var abs = Math.Abs(samples[i]);
            if (abs > max) max = abs;
        }
        return max;
    }

    private static double PowerToLufs(double power)
    {
        if (power <= 0) return double.NegativeInfinity;
        return -0.691 + 10.0 * Math.Log10(power);
    }

    private static Channel[] GetDefaultChannelMap(int channels)
    {
        return channels switch
        {
            1 => [Channel.Center],
            2 => [Channel.Left, Channel.Right],
            4 => [Channel.Left, Channel.Right, Channel.LeftSurround, Channel.RightSurround],
            5 => [Channel.Left, Channel.Right, Channel.Center, Channel.LeftSurround, Channel.RightSurround],
            6 => [Channel.Left, Channel.Right, Channel.Center, Channel.Unused, Channel.LeftSurround, Channel.RightSurround],
            _ => Enumerable.Range(0, channels).Select(i => i switch
            {
                0 => Channel.Left, 1 => Channel.Right, 2 => Channel.Center,
                3 => Channel.Unused, 4 => Channel.LeftSurround, 5 => Channel.RightSurround,
                _ => Channel.Unused
            }).ToArray()
        };
    }

    private static double GetChannelWeight(Channel channelType)
    {
        return channelType switch
        {
            Channel.LeftSurround or Channel.RightSurround or
            Channel.Mp060 or Channel.Mm060 or Channel.Mp090 or Channel.Mm090 or
            Channel.Up090 or Channel.Um090 or Channel.Up110 or Channel.Um110 or
            Channel.Up135 or Channel.Um135 or Channel.Up180 or Channel.Tp000 => 1.41,
            Channel.DualMono => 2.0,
            _ => 1.0
        };
    }

    public sealed class Session : IDisposable
    {
        private readonly int _channels;
        private readonly Channel[] _channelMap;
        private readonly KWeightingFilter _filter;
        private readonly List<double> _blockPowers;
        private readonly int _framesPerBlock;
        private readonly int _framesPerStep;
        
        private readonly float[][] _channelBuffers;
        private int _bufferFillCount;
        
        private double _peak;
        private bool _disposed;

        internal Session(int sampleRate, int channels, Channel[]? channelMap)
        {
            _channels = channels;
            _channelMap = channelMap ?? GetDefaultChannelMap(channels);
            _filter = new KWeightingFilter(sampleRate, channels);
            _blockPowers = new List<double>();
            _framesPerBlock = (int)(sampleRate * BlockDurationMs / 1000.0);
            _framesPerStep = (int)(_framesPerBlock * (1.0 - BlockOverlap));
            
            _channelBuffers = new float[channels][];
            for (var i = 0; i < channels; i++)
                _channelBuffers[i] = new float[_framesPerBlock];
                
            _bufferFillCount = 0;
            _peak = 0;
        }

        public double Peak => _peak;

        public void ProcessChunk(AudioChunk chunk)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(Session));
            if (chunk.Samples.Length == 0) return;

            var chunkPeak = CalculatePeakSimd(chunk.Samples);
            if (chunkPeak > _peak) _peak = chunkPeak;

            // 2. Optimized Filtering & Integrated Processing
            // Filter returns samples in channel-major order: [Ch0 samples, Ch1 samples, ...]
            float[] filteredNewSamples = ArrayPool<float>.Shared.Rent(chunk.Samples.Length);
            try
            {
                _filter.Process(chunk.Samples, filteredNewSamples);
                var samplesPerChannel = chunk.Samples.Length / _channels;

                int framesToProcess = samplesPerChannel;
                int offset = 0;

                while (framesToProcess > 0)
                {
                    int spaceInBuffer = _framesPerBlock - _bufferFillCount;
                    int take = Math.Min(spaceInBuffer, framesToProcess);

                    for (int ch = 0; ch < _channels; ch++)
                    {
                        ReadOnlySpan<float> source = filteredNewSamples.AsSpan(ch * samplesPerChannel + offset, take);
                        Span<float> target = _channelBuffers[ch].AsSpan(_bufferFillCount, take);
                        source.CopyTo(target);
                    }

                    _bufferFillCount += take;
                    offset += take;
                    framesToProcess -= take;

                    if (_bufferFillCount == _framesPerBlock)
                    {
                        ProcessFullBlock();
                        
                        // Sliding Window Shift: O(1) in terms of computational complexity per sample session
                        // Move overlap to the start
                        int overlap = _framesPerBlock - _framesPerStep;
                        for (int ch = 0; ch < _channels; ch++)
                        {
                            var span = _channelBuffers[ch].AsSpan();
                            span.Slice(_framesPerStep, overlap).CopyTo(span);
                        }
                        _bufferFillCount = overlap;
                    }
                }
            }
            finally
            {
                ArrayPool<float>.Shared.Return(filteredNewSamples);
            }
        }

        private void ProcessFullBlock()
        {
            var sum = 0.0;
            for (var ch = 0; ch < _channels; ch++)
            {
                var channelType = _channelMap[ch];
                if (channelType == Channel.Unused) continue;

                var channelSum = 0.0;
                var span = _channelBuffers[ch].AsSpan(0, _framesPerBlock);
                
                if (Vector.IsHardwareAccelerated && span.Length >= Vector<float>.Count)
                {
                    var vSum = Vector<float>.Zero;
                    var i = 0;
                    for (; i <= span.Length - Vector<float>.Count; i += Vector<float>.Count)
                    {
                        var v = new Vector<float>(span.Slice(i));
                        vSum += v * v;
                    }
                    float total = 0;
                    for (int j = 0; j < Vector<float>.Count; j++) total += vSum[j];
                    channelSum = total;
                    for (; i < span.Length; i++) channelSum += (double)span[i] * span[i];
                }
                else
                {
                    foreach (var sample in span) channelSum += (double)sample * sample;
                }

                sum += channelSum * GetChannelWeight(channelType);
            }

            var blockPower = sum / _framesPerBlock;
            if (PowerToLufs(blockPower) > AbsoluteGateThreshold)
                _blockPowers.Add(blockPower);
        }

        public double GetIntegratedLoudness()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(Session));
            if (_blockPowers.Count == 0) return double.NegativeInfinity;

            var ungatedMeanPower = _blockPowers.Average();
            var ungatedLoudness = PowerToLufs(ungatedMeanPower);
            var relativeThreshold = ungatedLoudness + RelativeGateThreshold;

            var gatedPowers = _blockPowers.Where(p => PowerToLufs(p) > relativeThreshold).ToList();
            if (gatedPowers.Count == 0) return double.NegativeInfinity;

            return PowerToLufs(gatedPowers.Average());
        }

        public void Dispose() => _disposed = true;
    }

    private class KWeightingFilter
    {
        private readonly double[] _b;
        private readonly double[] _a;
        private readonly double[][] _filterState;
        private readonly int _channels;
        private const int DenormalFlushInterval = 20000;

        public KWeightingFilter(int sampleRate, int channels)
        {
            _channels = channels;
            (_b, _a) = CalculateFilterCoefficients(sampleRate);
            _filterState = new double[channels][];
            for (var i = 0; i < channels; i++)
                _filterState[i] = new double[4];
        }

        private static (double[] b, double[] a) CalculateFilterCoefficients(double rate)
        {
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

            const double f0_rlb = 38.13547087602444;
            const double Q_rlb = 0.5003270373238773;
            K = Math.Tan(Math.PI * f0_rlb / rate);
            ra[1] = 2.0 * (K * K - 1.0) / (1.0 + K / Q_rlb + K * K);
            ra[2] = (1.0 - K / Q_rlb + K * K) / (1.0 + K / Q_rlb + K * K);

            var b = new double[5];
            var a = new double[5];
            b[0] = pb[0] * rb[0]; b[1] = pb[0] * rb[1] + pb[1] * rb[0]; b[2] = pb[0] * rb[2] + pb[1] * rb[1] + pb[2] * rb[0]; b[3] = pb[1] * rb[2] + pb[2] * rb[1]; b[4] = pb[2] * rb[2];
            a[0] = pa[0] * ra[0]; a[1] = pa[0] * ra[1] + pa[1] * ra[0]; a[2] = pa[0] * ra[2] + pa[1] * ra[1] + pa[2] * ra[0]; a[3] = pa[1] * ra[2] + pa[2] * ra[1]; a[4] = pa[2] * ra[2];
            return (b, a);
        }

        public void Process(float[] interleavedSamples, float[] output)
        {
            var framesPerChannel = interleavedSamples.Length / _channels;

            for (var ch = 0; ch < _channels; ch++)
            {
                var state = _filterState[ch];
                var channelOffset = ch * framesPerChannel;

                for (var f = 0; f < framesPerChannel; f++)
                {
                    double input = interleavedSamples[f * _channels + ch];
                    var filtered = _b[0] * input + state[0];
                    state[0] = _b[1] * input - _a[1] * filtered + state[1];
                    state[1] = _b[2] * input - _a[2] * filtered + state[2];
                    state[2] = _b[3] * input - _a[3] * filtered + state[3];
                    state[3] = _b[4] * input - _a[4] * filtered;

                    if (f % DenormalFlushInterval == 0)
                    {
                        for (var i = 0; i < 4; i++)
                            if (Math.Abs(state[i]) < 1e-15) state[i] = 0.0;
                    }
                    output[channelOffset + f] = (float)filtered;
                }
                for (var i = 0; i < 4; i++)
                    if (Math.Abs(state[i]) < 1e-15) state[i] = 0.0;
            }
        }
    }
}
