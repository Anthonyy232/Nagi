<div align="center">
    <img style="display: block; border-radius: 9999px;" src="https://github.com/user-attachments/assets/58f7a205-113d-463e-a2f3-d9979b935da1" alt="">
    <h1>
        Nagi
        <p><img style="display: block; border-radius: 9999px;" src="https://img.shields.io/github/downloads/Anthonyy232/Nagi/total" alt=""></p>
    </h1>
  <p align="center"></p>
    <p>Rediscover your local music collection with Nagi, a music player focused on speed, simplicity, and privacy. Featuring a resizable mini-player, interactive lyrics, and wide format support, Nagi is built with C# and WinUI 3 to offer a clean, native Fluent experience. It's beautiful, efficient, and respects your privacy.</p>
</div>

## Features
- **Fluent & Modern UI**: A beautiful and responsive interface built with the latest WinUI 3, featuring customizable backdrops like Mica, Mica Alt, and Acrylic.
- **Resizable Mini-Player**: A sleek, always-on-top mini-player for easy control while you work. Features playback controls, album art, a draggable interface, and an efficiency mode to reduce resource usage.
- **Lyrics Support**: View lyrics from embedded tags or external `.lrc` files. Click any line to seek directly to that part of the song.
- **Folder-Based Library**: Simply add your music folders, and Nagi will automatically scan and organize your collection.
- **Dynamic Theming**: The color scheme dynamically adapts to the art of the currently playing song.
- **Playlist Management**: Create, edit, and enjoy your own custom playlists.
- **Tray Controls**: Control your music from a convenient pop-up in the Windows system tray.
- **LastFM and Discord Integrations**: Automatically fetches artist metadata, scrobbles your tracks to Last.fm, and showcases your activity with improved Discord Rich Presence.
- **Private**: Your music and data are yours. Nagi does not send any user data to the cloud without user consent.
- **Wide Codec Support**: Nagi plays a vast array of audio formats, including: `.aa`, `.aax`, `.aac`, `.aiff`, `.ape`, `.dsf`, `.flac`, `.m4a`, `.m4b`, `.m4p`, `.mp3`, `.mpc`, `.mpp`, `.ogg`, `.oga`, `.wav`, `.wma`, `.wv`, and `.webm`.
      
<div align="center">
<table border="0">
  <tr>
    <td><img src="https://github.com/user-attachments/assets/a79450a7-d84e-4fe9-92b5-724b890e3e1d" width="100%" alt="library" /></td>
    <td><img src="https://github.com/user-attachments/assets/1387ccc4-d436-403b-8396-8888fbe1be26" width="100%" alt="album" /></td>
  </tr>
  <tr>
    <td><img src="https://github.com/user-attachments/assets/c99ac0a0-7484-4b6c-8113-e6abc731f879" width="100%" alt="artist" /></td>
    <td><img src="https://github.com/user-attachments/assets/ea46c4c4-8e22-4c45-9cd7-d275ea770e88" width="100%" alt="tray" /></td>
  </tr>
</table>
</div>

    


## Download
[<img src="https://get.microsoft.com/images/en-us%20dark.svg" alt="Download app from Microsoft Store" width="350">](https://apps.microsoft.com/detail/9P1V1PPML3QT?referrer=appbadge&launch=true&mode=full)
[<img src="https://github.com/user-attachments/assets/f81e6835-068d-4513-894b-659b5ac7f0ea" alt="Download app from GitHub" width="256">](https://github.com/Anthonyy232/Nagi/releases)



## Localization
Soon!

## Technologies

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

Thanks to the creators and maintainers of the open-source libraries used in this project.

## Build

This project is built using C# and the Windows App SDK. Here are the steps to build the project:

### Prerequisites
- Visual Studio 2022 or later
- The **".NET Desktop Development"** and **"Universal Windows Platform development"** workloads installed (includes Windows App SDK).
- .NET 8 SDK (or as specified in the project file)

### Steps
- Open a terminal and run the following git command:  <pre>git clone https://github.com/Anthonyy232/Nagi.git</pre>
- In File Explorer, navigate to the cloned repository and open Nagi.sln with Visual Studio.
- In Visual Studio, set the Solution Platform to x64 (or your target architecture).
- Press F5 or click the ▶ Nagi (Package) button to build and run the application.

## Contributions

All contributions to the app are welcome. Feel free to report any issues and create pull requests for any bug fixes or new features.

## Donations

If you enjoy using Nagi and want to support its development, you can do so using GitHub sponsorship (one-time or monthly). I appreciate the thought, thanks!

## License
Contact on my GitHub profile

[Anthony La](https://github.com/Anthonyy232]) © GNU GPL v3.0
