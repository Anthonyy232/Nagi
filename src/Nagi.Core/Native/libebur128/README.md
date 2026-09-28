# libebur128 native binaries

These DLLs are built from [libebur128 v1.2.6](https://github.com/jiixyj/libebur128/releases/tag/v1.2.6), commit `67b33abe1558160ed76ada1322329b0e9e058b02`. The distribution includes the upstream MIT license and the bundled `queue/sys/queue.h` BSD-3-Clause notice in [LICENSE](LICENSE). The source is not modified or vendored here. The pinned release fixes Windows shared-library linking. The application uses the upstream `ebur128_init`, `ebur128_add_frames_float`, `ebur128_loudness_global`, `ebur128_sample_peak`, and `ebur128_destroy` C exports. `EBUR128_MODE_I | EBUR128_MODE_SAMPLE_PEAK` is `21`; Windows `unsigned long` sample rate is 32-bit on both supported architectures.

Normal app builds use the checked-in DLLs. To rebuild the native library, run `build.ps1` once for `x64` and once for `ARM64`, passing a clean checkout of that commit as `-SourceRoot`. The script builds a shared library with the static MSVC runtime, checks the PE machine, runtime imports and required exports, then copies it into the matching architecture directory. Its build intermediate directory defaults to C: to keep large native build products out of the repository. It uses Visual Studio Community 2026 with the corresponding MSVC architecture tools. For example:

```powershell
./build.ps1 -Architecture x64 -SourceRoot C:\NagiPerfHostOutputs\libebur128-v1.2.6
./build.ps1 -Architecture ARM64 -SourceRoot C:\NagiPerfHostOutputs\libebur128-v1.2.6
```

The x64 DLL was built with MSVC 19.51.36244.0 and Windows SDK 10.0.28000.0. It imports only `KERNEL32.dll`. The ARM64 DLL was cross-compiled on x64 with official MSVC 19.51.36260.0 ARM64 packages and the same SDK, using `/MT /std:clatest`; it has PE machine `AA64`, imports only `KERNEL32.dll`, and exports the five required functions. Upstream's ARM64 non-SSE warning selects its manual subnormal filter-state flush; `/std:clatest` lets MSVC compile that warning without modifying upstream code. Static CRT avoids a new Visual C++ Redistributable dependency; FFmpeg remains an existing separate runtime tool.

The packaged x64 analyzer was tested with FFmpeg and real ReplayGain file/database writes. The ARM64 DLL and the exact published Core assembly were also executed in Windows ARM64 WinPE under QEMU: reference tones, gating, silence, short audio, final partial-window peaks and repeated native handle lifetimes matched x64. That ARM64 check covers the native API and C# interop, not FFmpeg decoding or the full WinUI application in WinPE.

| File | SHA-256 |
| --- | --- |
| `x64/ebur128.dll` | `B5D2312C5629DCB5F8C69BF721F42EEC20128833F49698F088B74CF6893BE891` |
| `arm64/ebur128.dll` | `A6A1F2518728B1AB3019FE84F7D3D1EC1D02BC50760AABBCC566CA63DDE2D551` |
