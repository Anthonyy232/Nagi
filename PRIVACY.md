# Privacy Policy for Nagi

Nagi is a music player designed with speed, simplicity, and user privacy in mind. This document explains how Nagi handles your data and interacts with third-party services.

## Local-First Philosophy

Nagi is primarily a local music player. Your music library, playlists, and playback history are stored locally on your device. The app does not upload your library to a Nagi account or cloud library.

## Third-Party Services (Opt-In)

Online metadata, online lyrics, scrobbling, and Discord Rich Presence are disabled by default. You can enable them in Settings. Application updates and optional romanization pack downloads are described separately below.

When you enable these services, Nagi sends the metadata needed for the request, such as artist names, album titles, track names, durations, and MusicBrainz IDs read from your tags, to the following providers:

### Metadata and Artwork
Nagi can fetch artist images, artist biographies, and album covers from the sources you leave enabled in Settings:
- **MusicBrainz**: To look up artist and album IDs that your tags don't include.
- **Wikipedia and Wikidata**: For artist biographies, requested in the app language.
- **Cover Art Archive**: For album covers of MusicBrainz releases.
- **Fanart.tv**: For artist images.
- **TheAudioDB**: For artist images and biographies.
- **Deezer**: For artist images and album covers.
- **Apple Music** (iTunes Search API): For album covers. Searches include your Windows region so they use your local catalog.
- **Last.fm**: For artist biographies and album covers.

Album covers are looked up only for albums without local artwork, except when Discord Rich Presence is also enabled (see below).

### Lyrics
Nagi can fetch lyrics for your tracks from:
- **LrcLib**: A community-driven lyrics database.
- **NetEase Music**: For synchronized and plain text lyrics.

### Scrobbling and Social
- **Last.fm Scrobbling**: If you choose to connect your Last.fm account, Nagi will send your playback history (track name, artist, album, album artist, track number, duration, timestamp, and the track's MusicBrainz ID when tagged) to Last.fm so you can track your listening habits.
- **ListenBrainz**: If enabled, Nagi sends listening history and, when separately enabled, the currently playing track to ListenBrainz or the compatible server you configure. Each listen includes the track details above, the release's MusicBrainz IDs when tagged, and Nagi's name and version. Requests use your user token to associate listening activity with your account.
- **Discord Rich Presence**: If enabled, Nagi will share your currently playing track details with your Discord client to display as your status. Discord can only show images from the web, so when online metadata is also enabled and Discord is running, Nagi looks up the playing album's cover with the album cover sources above and shares that image link with Discord.

### Application Updates and Downloads
- **Updates**: Microsoft Store installations use the Store's update mechanism. Installations through the GitHub `.appinstaller` file use Windows App Installer to check GitHub for updates on launch. These update mechanisms are managed by Windows, rather than an in-app update setting.
- **Romanization packs**: Browsing available packs retrieves a catalog from GitHub. Installing a pack downloads its dictionaries. Romanization runs locally; Nagi does not upload lyric text for this processing.
- **Provider access**: Features that require a shared provider API key retrieve it from Nagi's API server. This request identifies the provider; it does not upload your music library.

## Data Usage

Online requests include the metadata needed for the feature and, for account-based services, authentication credentials. Remote services also receive connection information such as your IP address. Their handling of these requests is governed by their own privacy policies.

## Changes to This Policy

We may update our Privacy Policy from time to time. We will notify you of any significant changes by updating the "Last Updated" date at the bottom of this document.

---
*Last Updated: October 24, 2026*
