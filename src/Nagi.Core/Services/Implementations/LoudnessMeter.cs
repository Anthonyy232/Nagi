using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using Nagi.Core.Services.Abstractions;

namespace Nagi.Core.Services.Implementations;

/// <summary>
///     Pure C# implementation of EBU R128 integrated loudness measurement.
///     Optimized for extreme performance with SIMD-accelerated peak detection,
///     O(1) sliding window buffers, zero-copy memory patterns, and allocation-free processing.
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
            float maxV = 0;
            for (int j = 0; j < Vector<float>.Count; j++)
                if (vMax[j] > maxV) maxV = vMax[j];
            max = maxV;
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

            // 1. SIMD Peak
            var chunkPeak = CalculatePeakSimd(chunk.Samples);
            if (chunkPeak > _peak) _peak = chunkPeak;

            // 2. Optimized Fused Filtering & Distribution
            var samplesPerChannel = chunk.Samples.Length / _channels;
            int offset = 0; 

            while (offset < samplesPerChannel)
            {
                int remainingFramesInChunk = samplesPerChannel - offset;
                int spaceInBuffer = _framesPerBlock - _bufferFillCount;
                int take = Math.Min(spaceInBuffer, remainingFramesInChunk);

                _filter.ProcessAndDistribute(
                    chunk.Samples.AsSpan(), 
                    _channelBuffers, 
                    _bufferFillCount,
                    offset,
                    take);

                _bufferFillCount += take;
                offset += take;

                if (_bufferFillCount == _framesPerBlock)
                {
                    ProcessFullBlock();
                    
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

            double sum = 0;
            int count = _blockPowers.Count;
            for(int i = 0; i < count; i++) sum += _blockPowers[i];
            
            var ungatedMeanPower = sum / count;
            var ungatedLoudness = PowerToLufs(ungatedMeanPower);
            var relativeThreshold = ungatedLoudness + RelativeGateThreshold;

            double gatedSum = 0;
            int gatedCount = 0;
            for(int i = 0; i < count; i++)
            {
                var p = _blockPowers[i];
                if (PowerToLufs(p) > relativeThreshold)
                {
                    gatedSum += p;
                    gatedCount++;
                }
            }

            if (gatedCount == 0) return double.NegativeInfinity;
            return PowerToLufs(gatedSum / gatedCount);
        }

        public void Dispose() => _disposed = true;
    }

    private class KWeightingFilter
    {
        private static readonly ConcurrentDictionary<double, (double[] b, double[] a)> _coefficientCache = new();
        
        private readonly double[] _b;
        private readonly double[] _a;
        private readonly double[][] _filterState;
        private readonly int _channels;
        
        private const int DenormalFlushInterval = 16384; 
        private const int DenormalFlushMask = DenormalFlushInterval - 1;

        public KWeightingFilter(int sampleRate, int channels)
        {
            _channels = channels;
            (_b, _a) = _coefficientCache.GetOrAdd((double)sampleRate, CalculateFilterCoefficients);
            
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

        public float[] Process(float[] interleavedSamples)
        {
            var frames = interleavedSamples.Length / _channels;
            var output = new float[interleavedSamples.Length];
            var outputWrappers = new float[_channels][];
            for (int i = 0; i < _channels; i++) outputWrappers[i] = new float[frames];
            
            ProcessAndDistribute(interleavedSamples, outputWrappers, 0, 0, frames);
            
            // Interleave back
            for (int ch = 0; ch < _channels; ch++) {
                var chSrc = outputWrappers[ch];
                for (int f = 0; f < frames; f++) output[ch * frames + f] = chSrc[f]; 
            }
            return output;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ProcessAndDistribute(
            ReadOnlySpan<float> inputInterleaved, 
            float[][] outputPlanar, 
            int writeOffset,
            int frameReadOffset,
            int count)
        {
            // Register blocking (caching array elements in locals)
            var b0 = _b[0]; var b1 = _b[1]; var b2 = _b[2]; var b3 = _b[3]; var b4 = _b[4];
            var a1 = _a[1]; var a2 = _a[2]; var a3 = _a[3]; var a4 = _a[4];
            int channels = _channels;

            // Specialized loop for Stereo (SIMD-ready logic & Register Blocking)
            if (channels == 2)
            {
                // Cache array references
                var stateL = _filterState[0];
                var stateR = _filterState[1];
                var outL = outputPlanar[0];
                var outR = outputPlanar[1];

                // Load state into registers (locals)
                double sL0 = stateL[0], sL1 = stateL[1], sL2 = stateL[2], sL3 = stateL[3];
                double sR0 = stateR[0], sR1 = stateR[1], sR2 = stateR[2], sR3 = stateR[3];

                // Check for SIMD support - process 2 channels in parallel using Vector128
                if (Vector128.IsHardwareAccelerated)
                {
                    var vB0 = Vector128.Create(b0); var vB1 = Vector128.Create(b1);
                    var vB2 = Vector128.Create(b2); var vB3 = Vector128.Create(b3); var vB4 = Vector128.Create(b4);
                    var vA1 = Vector128.Create(a1); var vA2 = Vector128.Create(a2);
                    var vA3 = Vector128.Create(a3); var vA4 = Vector128.Create(a4);

                    var vState0 = Vector128.Create(sL0, sR0);
                    var vState1 = Vector128.Create(sL1, sR1);
                    var vState2 = Vector128.Create(sL2, sR2);
                    var vState3 = Vector128.Create(sL3, sR3);
                    
                    var small = Vector128.Create(1e-15);

                    for (var f = 0; f < count; f++)
                    {
                        int inputIdx = (frameReadOffset + f) * 2;
                        
                        // Load input L/R
                        double inL = inputInterleaved[inputIdx];
                        double inR = inputInterleaved[inputIdx + 1];
                        var vIn = Vector128.Create(inL, inR);

                        var vFiltered = vB0 * vIn + vState0;
                        vState0 = vB1 * vIn - vA1 * vFiltered + vState1;
                        vState1 = vB2 * vIn - vA2 * vFiltered + vState2;
                        vState2 = vB3 * vIn - vA3 * vFiltered + vState3;
                        vState3 = vB4 * vIn - vA4 * vFiltered;

                        // Extract and write results
                        outL[writeOffset + f] = (float)vFiltered.GetElement(0);
                        outR[writeOffset + f] = (float)vFiltered.GetElement(1);

                        // Denormal check
                        if (((frameReadOffset + f) & DenormalFlushMask) == 0)
                        {
                            // Abs(vState) < small ? 0 : vState
                            // Using Vector128.Abs (requires .NET 7+)
                            if (Vector128.GreaterThanAll(small, Vector128.Abs(vState0))) vState0 = Vector128<double>.Zero;
                            if (Vector128.GreaterThanAll(small, Vector128.Abs(vState1))) vState1 = Vector128<double>.Zero;
                            if (Vector128.GreaterThanAll(small, Vector128.Abs(vState2))) vState2 = Vector128<double>.Zero;
                            if (Vector128.GreaterThanAll(small, Vector128.Abs(vState3))) vState3 = Vector128<double>.Zero;
                        }
                    }

                    // Write-back state
                    stateL[0] = vState0.GetElement(0); stateL[1] = vState1.GetElement(0); 
                    stateL[2] = vState2.GetElement(0); stateL[3] = vState3.GetElement(0);
                    stateR[0] = vState0.GetElement(1); stateR[1] = vState1.GetElement(1);
                    stateR[2] = vState2.GetElement(1); stateR[3] = vState3.GetElement(1);
                }
                else
                {
                    // Fallback to Scalar Register Blocking
                    for (var f = 0; f < count; f++)
                    {
                        int inputIdx = (frameReadOffset + f) * 2;
                        double inL = inputInterleaved[inputIdx];
                        double inR = inputInterleaved[inputIdx + 1];

                        // Left
                        var filtL = b0 * inL + sL0;
                        sL0 = b1 * inL - a1 * filtL + sL1;
                        sL1 = b2 * inL - a2 * filtL + sL2;
                        sL2 = b3 * inL - a3 * filtL + sL3;
                        sL3 = b4 * inL - a4 * filtL;
                        outL[writeOffset + f] = (float)filtL;

                        // Right
                        var filtR = b0 * inR + sR0;
                        sR0 = b1 * inR - a1 * filtR + sR1;
                        sR1 = b2 * inR - a2 * filtR + sR2;
                        sR2 = b3 * inR - a3 * filtR + sR3;
                        sR3 = b4 * inR - a4 * filtR;
                        outR[writeOffset + f] = (float)filtR;
                        
                        // Denormal check
                        if (((frameReadOffset + f) & DenormalFlushMask) == 0)
                        {
                             if (Math.Abs(sL0) < 1e-15) sL0 = 0; if (Math.Abs(sL1) < 1e-15) sL1 = 0;
                             if (Math.Abs(sL2) < 1e-15) sL2 = 0; if (Math.Abs(sL3) < 1e-15) sL3 = 0;
                             if (Math.Abs(sR0) < 1e-15) sR0 = 0; if (Math.Abs(sR1) < 1e-15) sR1 = 0;
                             if (Math.Abs(sR2) < 1e-15) sR2 = 0; if (Math.Abs(sR3) < 1e-15) sR3 = 0;
                        }
                    }

                    // Write-back state
                    stateL[0] = sL0; stateL[1] = sL1; stateL[2] = sL2; stateL[3] = sL3;
                    stateR[0] = sR0; stateR[1] = sR1; stateR[2] = sR2; stateR[3] = sR3;
                }
            }
            else
            {
                // Generic (Multi-channel)
                for (var f = 0; f < count; f++)
                {
                    int inputIdx = (frameReadOffset + f) * channels;
                    
                    for (var ch = 0; ch < channels; ch++)
                    {
                        double val = inputInterleaved[inputIdx + ch]; 
                        var state = _filterState[ch];
                        
                        var filtered = b0 * val + state[0];
                        state[0] = b1 * val - a1 * filtered + state[1];
                        state[1] = b2 * val - a2 * filtered + state[2];
                        state[2] = b3 * val - a3 * filtered + state[3];
                        state[3] = b4 * val - a4 * filtered;

                        if (((frameReadOffset + f) & DenormalFlushMask) == 0)
                        {
                             if (Math.Abs(state[0]) < 1e-15) state[0] = 0;
                             if (Math.Abs(state[1]) < 1e-15) state[1] = 0;
                             if (Math.Abs(state[2]) < 1e-15) state[2] = 0;
                             if (Math.Abs(state[3]) < 1e-15) state[3] = 0;
                        }
                        
                        outputPlanar[ch][writeOffset + f] = (float)filtered;
                    }
                }
            }
        }
    }
}
