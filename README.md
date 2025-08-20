<div align="center">
  <img src="https://github.com/user-attachments/assets/58f7a205-113d-463e-a2f3-d9979b935da1" alt="Nagi Logo">
    
  # Nagi 

  **Rediscover your local music collection. A fast, private, and modern music player for Windows.**
  
<div align="center">
    <a href="https://github.com/Anthonyy232/Nagi/stargazers"><img src="https://img.shields.io/github/stars/Anthonyy232/Nagi?style=flat-square" alt="GitHub Stars"></a>
    <a href="https://github.com/Anthonyy232/Nagi/releases"><img src="https://img.shields.io/github/downloads/Anthonyy232/Nagi/total?style=flat-square&color=52c65f" alt="Total Downloads"></a>
    <a href="https://github.com/Anthonyy232/Nagi/releases/latest"><img src="https://img.shields.io/github/v/release/Anthonyy232/Nagi?style=flat-square" alt="Latest Release"></a>
    <a href="https://github.com/Anthonyy232/Nagi/blob/master/LICENSE"><img src="https://img.shields.io/github/license/Anthonyy232/Nagi?style=flat-square" alt="License"></a>
    <img src="https://img.shields.io/badge/Platform-Windows-0078D6?style=flat-square&logo=windows" alt="Platform">
    <img src="https://img.shields.io/badge/C%23-239120?style=flat-square&logo=c-sharp&logoColor=white" alt="C#">
    <img src="https://img.shields.io/badge/WinUI_3-59278F?style=flat-square&logo=windows&logoColor=white" alt="WinUI 3">
    <img src="https://img.shields.io/badge/.NET-512BD4?style=flat-square&logo=dotnet&logoColor=white" alt=".NET">
</div>

</div>

<div>
    <br>
    <p>
    Nagi is a music player focused on speed, simplicity, and privacy. Featuring a resizable mini-player, interactive lyrics, and wide format support, Nagi is built with C# and WinUI 3 to offer a clean, native Fluent experience. It's beautiful, efficient, and respects your privacy.
    </p>
</div>


<div align="center">
  <img src="https://github.com/user-attachments/assets/a79450a7-d84e-4fe9-92b5-724b890e3e1d" alt="Nagi Library View" width="800" style="border-radius: 8px;">
</div>

## ✨ Features
- **Fluent & Modern UI**: A beautiful and responsive interface built with the latest WinUI 3, featuring customizable backdrops like Mica, Mica Alt, and Acrylic.
- **Resizable Mini-Player**: A sleek, always-on-top mini-player for easy control while you work. Features playback controls, album art, a draggable interface, and an efficiency mode to reduce resource usage.
- **Lyrics Support**: View lyrics from embedded tags or external `.lrc` files. Click any line to seek directly to that part of the song.
- **Folder-Based Library**: Simply add your music folders, and Nagi will automatically scan and organize your collection.
- **Dynamic Theming**: The color scheme dynamically adapts to the art of the currently playing song.
- **Playlist Management**: Create, edit, and enjoy your own custom playlists.
- **Tray Controls**: Control your music from a convenient pop-up in the Windows system tray.
- **Last.fm and Discord Integrations**: Automatically fetches artist metadata from Last.fm, scrobbles your tracks, and showcases your activity with improved Discord Rich Presence.
- **Private by Design**: Your music and data are yours. Nagi does not send any user data to the cloud without your consent.
- **Wide Codec Support**: Nagi plays a vast array of audio formats, including: `.aa`, `.aax`, `.aac`, `.aiff`, `.ape`, `.dsf`, `.flac`, `.m4a`, `.m4b`, `.m4p`, `.mp3`, `.mpc`, `.mpp`, `.ogg`, `.oga`, `.wav`, `.wma`, `.wv`, and `.webm`.

## 🖼️ Screenshots

<div align="center">
  <table border="0" cellspacing="10">
    <tr>
      <td><img src="https://github.com/user-attachments/assets/1387ccc4-d436-403b-8396-8888fbe1be26" width="100%" alt="album" /></td>
      <td><img src="https://github.com/user-attachments/assets/c99ac0a0-7484-4b6c-8113-e6abc731f879" width="100%" alt="artist" /></td>
      <td><img src="https://github.com/user-attachments/assets/ea46c4c4-8e22-4c45-9cd7-d275ea770e88" width="100%" alt="tray" /></td>
    </tr>
  </table>
</div>

## 📥 Download
<div>
  <a href="https://apps.microsoft.com/detail/9P1V1PPML3QT?referrer=appbadge&launch=true&mode=full">
    <img src="https://get.microsoft.com/images/en-us%20dark.svg" alt="Download from Microsoft Store" width="300">
  </a>
  &nbsp;&nbsp;&nbsp;&nbsp;
  <a href="https://github.com/Anthonyy232/Nagi/releases">
    <img src="https://github.com/user-attachments/assets/f81e6835-068d-4513-894b-659b5ac7f0ea" alt="Download from GitHub" width="220">
  </a>
</div>

## 🌍 Localization
Localization support is planned for a future release. Contributions to add new languages are welcome!

## 🛠️ Technologies
- **[C#](https://docs.microsoft.com/en-us/dotnet/csharp/)** & **[.NET](https://dotnet.microsoft.com/)**: The core programming language and framework for building robust Windows applications.
- **[WinUI 3](https://docs.microsoft.com/en-us/windows/apps/winui/winui3/)** in **[Windows App SDK](https://github.com/microsoft/WindowsAppSDK)**: The native UI platform for crafting modern, fluent interfaces on Windows.
- **[LibVLCSharp](https://github.com/videolan/libvlcsharp)**: A cross-platform .NET binding for LibVLC, enabling robust and wide-ranging audio format support.
- **[Community Toolkit for WinUI](https://github.com/CommunityToolkit/WindowsCommunityToolkit)**: A collection of controls, helpers, and services to simplify app development (e.g., ColorPicker, SettingsControls).
- **[Community Toolkit MVVM](https://docs.microsoft.com/en-us/dotnet/communitytoolkit/mvvm/)**: A modern, fast, and platform-agnostic MVVM library.
- **[Entity Framework Core (SQLite)](https://docs.microsoft.com/en-us/ef/core/)**: A modern object-relational mapper for .NET, used for local database storage.
- **[TagLib#](https://github.com/mono/taglib-sharp)**: A library for reading and writing metadata in audio files.
- **[MaterialColorUtilities](https://github.com/material-foundation/material-color-utilities)**: For generating dynamic color schemes from artwork.
- **[H.NotifyIcon.WinUI](https://github.com/HavenDV/H.NotifyIcon)**: For creating and managing the Windows tray icon.
- **[Microsoft Dependency Injection](https://docs.microsoft.com/en-us/dotnet/core/extensions/dependency-injection)**: For implementing a loosely coupled and testable architecture.

*Thanks to the creators and maintainers of all the open-source libraries that make Nagi possible.*

## 🚀 Build from Source

This project is built using C# and the Windows App SDK.

### Prerequisites
- Visual Studio 2022 or later
- The **".NET Desktop Development"** and **"Universal Windows Platform development"** workloads installed (includes Windows App SDK).
- .NET 8 SDK (or as specified in the project file)

### Steps
1.  Clone the repository:
    ```bash
    git clone https://github.com/Anthonyy232/Nagi.git
    ```
2.  Navigate to the cloned directory and open `Nagi.sln` with Visual Studio.
3.  In Visual Studio, set the Solution Platform to `x64` (or your target architecture).
4.  Press `F5` or click the `▶ Nagi (Package)` button to build and run the application.

## 🤝 Contributions
All contributions are welcome! Feel free to report issues, suggest features, or create pull requests for bug fixes and new features.

## ❤️ Support
If you enjoy using Nagi and want to support its development, you can do so via GitHub Sponsors. Your support is greatly appreciated!

<div align="center">
  <a href="https://github.com/sponsors/Anthonyy232"><img src="https://img.shields.io/badge/Sponsor_on_GitHub-d75594?style=for-the-badge&logo=github&logoColor=white" alt="Sponsor on GitHub"></a>
</div>

## 📄 License
This project is licensed under the **GNU General Public License v3.0**. See the [LICENSE](LICENSE) file for details.
