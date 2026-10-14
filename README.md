<div align="center">

<img src="media/AppLogo.png" alt="Nagi icon" width="120">

# Nagi

**Rediscover your local music collection.**<br>
A fast, private, and modern music player for Windows.

<p>
  <a href="https://github.com/Anthonyy232/Nagi/stargazers"><img src="https://img.shields.io/github/stars/Anthonyy232/Nagi?style=flat-square" alt="GitHub Stars"></a>
  <a href="https://github.com/Anthonyy232/Nagi/releases"><img src="https://img.shields.io/github/downloads/Anthonyy232/Nagi/total?style=flat-square&color=52c65f" alt="Total Downloads"></a>
  <a href="https://github.com/Anthonyy232/Nagi/releases/latest"><img src="https://img.shields.io/github/v/release/Anthonyy232/Nagi?style=flat-square" alt="Latest Release"></a>
  <a href="https://github.com/Anthonyy232/Nagi/blob/master/LICENSE"><img src="https://img.shields.io/github/license/Anthonyy232/Nagi?style=flat-square" alt="License"></a>
  <a href="https://github.com/Anthonyy232/Nagi/actions/workflows/build.yml"><img src="https://img.shields.io/github/actions/workflow/status/Anthonyy232/Nagi/build.yml?style=flat-square&label=CI" alt="CI"></a>
  <a href="https://crowdin.com/project/nagi"><img src="https://badges.crowdin.net/nagi/localized.svg" alt="Crowdin"></a>
</p>
<p>
  <img src="https://img.shields.io/badge/Windows_10_%7C_11-0078D6?style=flat-square&logo=windows&logoColor=white" alt="Windows 10 | 11">
  <img src="https://img.shields.io/badge/x64_%7C_ARM64-444?style=flat-square" alt="x64 | ARM64">
  <img src="https://img.shields.io/badge/WinUI_3-59278F?style=flat-square" alt="WinUI 3">
  <img src="https://img.shields.io/badge/.NET_10-512BD4?style=flat-square&logo=dotnet&logoColor=white" alt=".NET 10">
</p>

<p>
  <a href="#-download">Download</a> ·
  <a href="#-features">Features</a> ·
  <a href="#%EF%B8%8F-screenshots">Screenshots</a> ·
  <a href="#-build-from-source">Build</a> ·
  <a href="#-contributing">Contributing</a> ·
  <a href="PRIVACY.md">Privacy</a>
</p>

<img src="https://github.com/user-attachments/assets/58f7a205-113d-463e-a2f3-d9979b935da1" alt="Nagi: rediscover your music" width="100%">

</div>

<br>

Nagi is a local-first music player built with C# and WinUI 3. It scans the folders you choose, keeps your library up to date, and plays almost any audio format through LibVLC. Lyrics, a resizable mini-player, smart playlists, a 10-band equalizer, and dynamic theming come built in. Nothing leaves your machine unless you turn on an online service, and the [privacy policy](PRIVACY.md) explains exactly what each one sends.

## 📥 Download

<div align="center">
  <a href="https://apps.microsoft.com/detail/9P1V1PPML3QT?referrer=appbadge&launch=true&mode=full">
    <img src="https://get.microsoft.com/images/en-us%20dark.svg" alt="Download from Microsoft Store" height="80">
  </a>
  &nbsp;&nbsp;
  <a href="https://github.com/Anthonyy232/Nagi/releases">
    <img src="https://github.com/user-attachments/assets/f81e6835-068d-4513-894b-659b5ac7f0ea" alt="Download from GitHub" height="80">
  </a>
</div>

<br>

Nagi runs on Windows 10 (1809 or later) and Windows 11, on x64 and ARM64. The Microsoft Store build updates through the Store. The GitHub release is a signed MSIX bundle that checks GitHub for new versions on its own.

## ✨ Features

<table>
  <tr>
    <td width="50%" valign="top">
      <h3>🎵 Your library, your files</h3>
      Point Nagi at your folders and it scans, organizes, and keeps everything current. Plays MP3, FLAC, AAC, OGG, WAV, AIFF, APE, DSD, M4A, WMA, WavPack, and more.
    </td>
    <td width="50%" valign="top">
      <h3>🎨 Fluent, and it matches your music</h3>
      Mica, Mica Alt, and Acrylic backdrops with smooth animations. The color scheme adapts in real time to the artwork of the song that is playing.
    </td>
  </tr>
  <tr>
    <td width="50%" valign="top">
      <h3>📝 Lyrics, synced and romanized</h3>
      Reads embedded tags, sidecar <code>.lrc</code> files, or optional online providers. Tap a synced line to jump there. Downloadable packs romanize Japanese, Korean, and Hindi lyrics locally.
    </td>
    <td width="50%" valign="top">
      <h3>🪟 Resizable mini-player</h3>
      An always-on-top mini-player with playback controls and album art. Efficiency mode keeps resource use low while it sits in the corner.
    </td>
  </tr>
  <tr>
    <td width="50%" valign="top">
      <h3>🔀 Smarter shuffles</h3>
      Smart playlists that update themselves, listening mixes that space out artists and resurface forgotten tracks, and "Play for…" queues that fit a chosen duration.
    </td>
    <td width="50%" valign="top">
      <h3>🔊 Sound that stays consistent</h3>
      A 10-band equalizer with preamp, ReplayGain volume normalization measured locally with libebur128, and adjustable playback speed.
    </td>
  </tr>
  <tr>
    <td width="50%" valign="top">
      <h3>🔒 Private by default</h3>
      Every online service is opt-in: Last.fm and ListenBrainz scrobbling, Discord Rich Presence, artist metadata, and online lyrics. The <a href="PRIVACY.md">privacy policy</a> lists exactly what each one sends.
    </td>
    <td width="50%" valign="top">
      <h3>🖥️ Feels like part of Windows</h3>
      System tray popup, media keys, system transport controls, taskbar playback buttons and shortcuts, and a sleep timer that keeps running while minimized.
    </td>
  </tr>
</table>

<details>
<summary><b>Full feature list</b></summary>
<br>

**Library**

- **Folder-based library**: Add your music folders and Nagi scans, organizes, and keeps the collection up to date as files change.
- **Wide format support**: Play MP3, FLAC, AAC, OGG, WAV, AIFF, APE, DSD, M4A, WMA, WavPack, and more through LibVLC.
- **Playlists and smart playlists**: Build playlists by hand, or write rule-based smart playlists that update themselves as your library grows. Export playlists to M3U.
- **Favorites and rediscovery**: Favorite songs from their menus or with Ctrl+D. An optional Favorites page lives under Settings → Customize Navigation. A dismissible library suggestion surfaces an often-played song you have not heard in over 90 days. Favorites stay in Nagi's database and never modify your audio files.
- **Listening history and insights**: Search recent listens from the Up Next header or replay a track without losing the queue. Earlier listens load as you scroll, and the Insights page summarizes your listening habits.

**Playback**

- **Queue management**: Drop files or folders onto the player to queue them without importing them. The Up Next side pane shows and reorders the queue with shuffle and repeat controls.
- **Listening mixes**: The shuffle dropdown offers three mixes built only from your local library and history. **More Variety** skips tracks played in the past day and spaces out artists, **Rediscover** picks tracks not heard in over 30 days, and **Unheard** plays songs you have never played in Nagi.
- **Play for…**: Fill a queue with whole songs that fit a chosen duration from the current list. Timed mixes turn repeat off and stop at the end of the queue.
- **Sleep timer**: Stop after the current track, at the end of the queue, or after a preset or custom duration. The active timer stays visible in the player and keeps running while minimized.
- **10-band equalizer** with preamp, and **ReplayGain volume normalization** measured locally with libebur128.
- **Playback speed**: Play at 0.5× to 2× from the player's options menu without changing pitch.
- **System tray and media controls**: Control playback from the tray popup, Windows media keys, the system transport controls, and the previous, play/pause, and next buttons in the taskbar preview.
- **Taskbar shortcuts**: Right-click the taskbar icon to shuffle your whole library, or to open the mini-player when Minimize to Mini-Player is on.

**Lyrics and look**

- **Lyrics**: Read lyrics from embedded tags, sidecar `.lrc` files, or optional online providers, with plain-text fallback when synced lyrics are unavailable. Tap a synced line to jump to that moment.
- **Romanization**: Show romanized lyrics for Japanese, Korean, and Hindi using downloadable packs. Romanization runs entirely on your machine.
- **Resizable mini-player**: An always-on-top mini-player with playback controls, album art, and an efficiency mode for low resource usage.
- **Fluent UI with dynamic theming**: Mica, Mica Alt, and Acrylic backdrops, smooth animations, and a color scheme that adapts in real time to the artwork of the playing song.

**Optional online services** (all off by default, all described in the [privacy policy](PRIVACY.md))

- **Last.fm and ListenBrainz** scrobbling, with an offline queue that retries later.
- **Discord Rich Presence** to show what you are listening to.
- **Artist and album metadata** from Last.fm, MusicBrainz, fanart.tv, and TheAudioDB.
- **Online lyrics** from LRCLIB and NetEase.

</details>

## 🖼️ Screenshots

<div align="center">
<table>
  <tr>
    <td align="center" colspan="2">
      <img src="https://github.com/user-attachments/assets/a79450a7-d84e-4fe9-92b5-724b890e3e1d" alt="Library view" width="100%"><br>
      <sub><b>Library</b> with the bottom player and Up Next</sub>
    </td>
  </tr>
  <tr>
    <td align="center" width="50%">
      <img src="https://github.com/user-attachments/assets/1387ccc4-d436-403b-8396-8888fbe1be26" alt="Album view" width="100%"><br>
      <sub><b>Album view</b> with dynamic theming from the artwork</sub>
    </td>
    <td align="center" width="50%">
      <img src="https://github.com/user-attachments/assets/c99ac0a0-7484-4b6c-8113-e6abc731f879" alt="Artist view" width="100%"><br>
      <sub><b>Artist view</b> with optional online metadata</sub>
    </td>
  </tr>
  <tr>
    <td align="center" colspan="2">
      <img src="https://github.com/user-attachments/assets/ea46c4c4-8e22-4c45-9cd7-d275ea770e88" alt="System tray popup" width="50%"><br>
      <sub><b>System tray popup</b> for quick playback control</sub>
    </td>
  </tr>
</table>
</div>

## 🌍 Localization

Nagi's translations live on [Crowdin](https://crowdin.com/project/nagi). New languages and corrections are always welcome, and no code changes are needed to contribute.

<div align="center">
  <a href="https://crowdin.com/project/nagi"><img src="https://img.shields.io/badge/Help_translate_Nagi-2E3340?style=for-the-badge&logo=crowdin&logoColor=white" alt="Help translate Nagi on Crowdin"></a>
</div>

## 🛠️ Technologies

| | |
| --- | --- |
| **[C#](https://learn.microsoft.com/en-us/dotnet/csharp/)** and **[.NET 10](https://dotnet.microsoft.com/)** | The language and runtime for the whole solution. |
| **[WinUI 3](https://learn.microsoft.com/en-us/windows/apps/winui/winui3/)** in the **[Windows App SDK](https://github.com/microsoft/WindowsAppSDK)** | The native Fluent UI platform for Windows. |
| **[LibVLCSharp](https://github.com/videolan/libvlcsharp)** | .NET bindings for LibVLC, which provides decoding and the equalizer. |
| **[libebur128](https://github.com/jiixyj/libebur128)** | Native loudness measurement for ReplayGain, with FFmpeg providing audio decoding. Binary provenance and rebuild steps are in [Native/libebur128](src/Nagi.Core/Native/libebur128/README.md). |
| **[Community Toolkit for WinUI](https://github.com/CommunityToolkit/Windows)** and **[MVVM](https://learn.microsoft.com/en-us/dotnet/communitytoolkit/mvvm/)** | Controls, helpers, and the MVVM source generators. |
| **[Entity Framework Core (SQLite)](https://learn.microsoft.com/en-us/ef/core/)** | The local library database. |
| **[ATL (Audio Tools Library)](https://github.com/Zeugma440/atldotnet)** | Reads and writes audio metadata. |
| **[ModernLrc](https://github.com/Anthonyy232/ModernLrcParser)** | Parses LRC lyrics files. |
| **[SixLabors.ImageSharp](https://github.com/SixLabors/ImageSharp)** | Artwork processing. |
| **[MaterialColorUtilities](https://github.com/material-foundation/material-color-utilities)** | Generates dynamic color schemes from artwork. |
| **[H.NotifyIcon.WinUI](https://github.com/HavenDV/H.NotifyIcon)** | The Windows tray icon. |
| **[Serilog](https://serilog.net/)** | Structured logging for diagnostics. |
| **[XAML Behaviors](https://github.com/Microsoft/XamlBehaviors)** | Declarative UI behaviors in XAML. |
| **[MSIX App Installer](https://learn.microsoft.com/windows/msix/app-installer/app-installer-file-overview)** | Automatic updates from GitHub releases. |
| **[Microsoft Dependency Injection](https://learn.microsoft.com/en-us/dotnet/core/extensions/dependency-injection)** | Loosely coupled, testable composition. |

*Thanks to the creators and maintainers of all the open-source libraries that make Nagi possible.*

## 🗂️ Project Structure

The solution is split so that everything except the Windows shell targets plain `net10.0` and can be tested without a XAML application.

| Path | Purpose |
| --- | --- |
| `src/Nagi.Core` | Library scanning, the EF Core database and migrations, playback queue, playlists, metadata and lyrics providers, scrobbling, and ReplayGain analysis. |
| `src/Nagi.Presentation` | Platform-neutral view models, app services such as settings and navigation, the abstractions each UI head implements, and the localized strings. References no UI framework. |
| `src/Nagi.Audio.LibVlc` | The LibVLC implementation of the audio player. Each UI head supplies the native LibVLC binaries for its platform. |
| `src/Nagi.WinUI` | The Windows app: XAML pages, windows, tray icon, system media controls, MSIX packaging, and DI registration. |
| `tests/` | `Nagi.Core.Tests`, `Nagi.Presentation.Tests`, and `Nagi.WinUI.Tests`. |
| `benchmarks/Nagi.Benchmarks` | BenchmarkDotNet suites for library scanning and queries. See its [README](benchmarks/Nagi.Benchmarks/README.md). |
| `tools/` | The romanization pack builder and the published packs. |
| `api/NagiAppFunctions` | A small Azure Functions app that hands out third-party API keys so they are not shipped in the client. |

## 🚀 Build from Source

**Prerequisites**

- Visual Studio 2026 (18.0) or later, as required for [targeting .NET 10](https://learn.microsoft.com/en-us/dotnet/core/porting/versioning-sdk-msbuild-vs).
- The **.NET Desktop Development** and **Universal Windows Platform development** workloads, which include the Windows App SDK.
- .NET 10 SDK 10.0.401 or later, matching `global.json`.
- `ffmpeg` on `PATH` if you want to run the loudness analysis tests or the scan benchmarks.

**Steps**

1. Clone the repository:

   ```bash
   git clone https://github.com/Anthonyy232/Nagi.git
   ```
2. Open `Nagi.sln` in Visual Studio.
3. Set the Solution Platform to `x64` or `ARM64`.
4. Press `F5` or click `▶ Nagi (Package)` to build and run.

<details>
<summary><b>Build an unsigned MSIX bundle from the command line</b></summary>
<br>

Run these from `src/Nagi.WinUI`:

```bash
# 1. Publish both platforms (enables ReadyToRun)
dotnet publish Nagi.WinUI.csproj -c Release -r win-x64 -p:Platform=x64 -p:PublishReadyToRun=true -p:GenerateAppxPackageOnBuild=false
dotnet publish Nagi.WinUI.csproj -c Release -r win-arm64 -p:Platform=ARM64 -p:PublishReadyToRun=true -p:GenerateAppxPackageOnBuild=false
# 2. Package the bundle
dotnet msbuild Nagi.WinUI.csproj -p:Configuration=Release -p:Platform=x64 -p:GenerateAppxPackageOnBuild=true -p:AppxBundle=Always "-p:AppxBundlePlatforms=x64|ARM64" -p:AppxPackageSigningEnabled=False
```

GitHub releases are signed through SignPath in CI. Microsoft Store uploads use the Store publisher identity and stay unsigned locally.

</details>

<details>
<summary><b>Run the tests</b></summary>
<br>

Run the three suites from the repository root:

```powershell
dotnet test --project tests/Nagi.Core.Tests/Nagi.Core.Tests.csproj -c Release
dotnet test --project tests/Nagi.Presentation.Tests/Nagi.Presentation.Tests.csproj -c Release
dotnet test --project tests/Nagi.WinUI.Tests/Nagi.WinUI.Tests.csproj -c Release -p:Platform=x64
```

- **Nagi.Core.Tests** covers services, the database, metadata providers, and scrobbling. The loudness analysis tests need `ffmpeg` on `PATH`.
- **Nagi.Presentation.Tests** covers the view models and app services on plain `net10.0`, with no Windows dependencies.
- **Nagi.WinUI.Tests** runs against the app assembly without launching a XAML application and covers activation, incremental loading, DI registration, and WinUI helpers.

The test projects use Microsoft.Testing.Platform with xUnit's native runner, so they show up in Visual Studio 2026 Test Explorer without VSTest adapters. All three suites run on every pull request to `master`.

</details>

<details>
<summary><b>Add a database migration</b></summary>
<br>

After changing the EF Core model:

```bash
dotnet ef migrations add <name> --project src\Nagi.Core --startup-project src\Nagi.Core --context MusicDbContext --no-build
```

</details>

## 🤝 Contributing

All contributions are welcome. Report issues, suggest features, or open pull requests for bug fixes and new features. Translations go through [Crowdin](https://crowdin.com/project/nagi).

## ❤️ Support

If you enjoy using Nagi and want to support its development, you can do so via GitHub Sponsors. Your support is greatly appreciated!

<div align="center">
  <a href="https://github.com/sponsors/Anthonyy232"><img src="https://img.shields.io/badge/Sponsor_on_GitHub-d75594?style=for-the-badge&logo=github&logoColor=white" alt="Sponsor on GitHub"></a>
</div>

## 📄 License

This project is licensed under the **GNU General Public License v3.0**. See the [LICENSE](LICENSE) file for details.
