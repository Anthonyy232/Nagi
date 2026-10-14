using System.Runtime.InteropServices;

namespace Nagi.Core.Services.Implementations;

internal sealed class Ebur128Meter : SafeHandle
{
    private const int BytesPerFrame = 2 * sizeof(float);

    public Ebur128Meter() : base(IntPtr.Zero, ownsHandle: true)
    {
        const int integrated = (1 << 2) | 1;
        const int samplePeak = (1 << 4) | 1;
        SetHandle(Init(2, 48000, integrated | samplePeak));
        if (IsInvalid) throw new OutOfMemoryException("Could not allocate the ReplayGain loudness meter.");
    }

    public override bool IsInvalid => handle == IntPtr.Zero;

    public unsafe void AddFrames(ReadOnlySpan<byte> samples)
    {
        if (samples.Length % BytesPerFrame != 0)
            throw new InvalidDataException("FFmpeg returned an incomplete stereo audio frame.");

        fixed (byte* data = samples)
            Check(AddFramesFloat(this, (float*)data, (nuint)(samples.Length / BytesPerFrame)));
    }

    public (double LoudnessLufs, double Peak)? GetResult()
    {
        Check(LoudnessGlobal(this, out var loudness));
        Check(SamplePeak(this, 0, out var left));
        Check(SamplePeak(this, 1, out var right));
        return double.IsFinite(loudness) && loudness > -70 ? (loudness, Math.Max(left, right)) : null;
    }

    protected override bool ReleaseHandle()
    {
        Destroy(ref handle);
        return true;
    }

    private static void Check(int error)
    {
        if (error != 0) throw new InvalidOperationException($"libebur128 failed with error {error}.");
    }

    [DllImport("ebur128", EntryPoint = "ebur128_init", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr Init(uint channels, uint sampleRate, int mode);

    [DllImport("ebur128", EntryPoint = "ebur128_destroy", CallingConvention = CallingConvention.Cdecl)]
    private static extern void Destroy(ref IntPtr state);

    [DllImport("ebur128", EntryPoint = "ebur128_add_frames_float", CallingConvention = CallingConvention.Cdecl)]
    private static extern unsafe int AddFramesFloat(Ebur128Meter state, float* samples, nuint frames);

    [DllImport("ebur128", EntryPoint = "ebur128_loudness_global", CallingConvention = CallingConvention.Cdecl)]
    private static extern int LoudnessGlobal(Ebur128Meter state, out double loudness);

    [DllImport("ebur128", EntryPoint = "ebur128_sample_peak", CallingConvention = CallingConvention.Cdecl)]
    private static extern int SamplePeak(Ebur128Meter state, uint channel, out double peak);
}
