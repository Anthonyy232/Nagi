using System;
using System.Runtime.InteropServices;
using LibVLCSharp;

namespace Nagi.WinUI.Helpers;

internal static class LibVlcAudioOutputInterop
{
    // LibVLCSharp's current ToUtf8 converts "" into NULL, which LibVLC 4 rejects.
    // Windows' MMDevice backend uses a non-null empty string for default routing.
    [DllImport("libvlc.dll", CallingConvention = CallingConvention.Cdecl,
        EntryPoint = "libvlc_audio_output_device_set")]
    private static extern int SetOutputDevice(IntPtr player,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string deviceId);

    internal static bool UseSystemDefault(MediaPlayer player) =>
        SetOutputDevice(player.NativeReference, string.Empty) == 0;
}
