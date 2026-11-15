# Nagi media

The images Nagi's README, website, app package and releases use, and the scripts that draw them. Kept off `master` so it stays light.

| Path | What it is |
| --- | --- |
| `readme/build.cjs` | Draws the README panels, the site images and the app icon set into a Nagi checkout. |
| `readme/inputs/` | The app screenshots the panels and banners are made from. |
| `releases/banner.cjs`, `releases/banners.json` | Draws the 1280x640 release banners, `releases/<tag>.png`. |
| `common.cjs` | Fonts, colours and page helpers both scripts share. |
| `fonts/` | Instrument Serif, the headline font, under the SIL Open Font License (`fonts/OFL.txt`). |
| `issues/` | Evidence images linked from bug reports. |

## Setup

Windows, because the body text is Segoe UI and the labels Cascadia Code or Consolas from the system. Instrument Serif comes from `fonts/`; nothing is fetched while drawing.

```sh
git worktree add ../nagi-media media     # from a Nagi checkout
cd ../nagi-media
npm ci
npx playwright install chromium
```

Both scripts take the path of a Nagi checkout on `master` as their first argument. They read the logo from its `media/AppLogo.svg` and `media/AppLogo-small.svg`, so the logo has one source.

## README, site and app icons

```sh
node readme/build.cjs <nagi>
```

It writes, into `<nagi>`:

- `.github/readme/hero-{light,dark}.png` and `feature-{library,lyrics,color,insights}-{light,dark}.png`, at 1.6x with transparent rounded corners.
- `site/img/*.webp`, the stage part of each panel, and `site/img/social.png`, the 1280x640 link preview.
- `src/Nagi.WinUI/Assets/`: `AppLogo.png`, the 50 tile, splash, store and taskbar PNGs, and `AppLogo.ico` (the five unplated target sizes). The logo is drawn from `AppLogo-small.svg` where it is under 40px.

Then commit the changed files on `master`. A run with nothing changed leaves the checkout clean.

## Release banners

```sh
node releases/banner.cjs <nagi> X.Y.Z "Short *highlight*" [scene]
```

The words between asterisks take the accent colour. Scenes: `mark` (the logo, the default), `library`, `queue`, `albums`, `lyrics`, `insights`, `player`. The banner is recorded in `releases/banners.json`; `node releases/banner.cjs <nagi>` with no tag redraws them all. Look at `releases/X.Y.Z.png`, commit and push `media`, and open the release notes with:

```
![Nagi X.Y.Z](https://raw.githubusercontent.com/Anthonyy232/Nagi/media/releases/X.Y.Z.png)
```

## Screenshots

`readme/inputs/` holds the app's own pixels; the scripts place them 1:1 on a flat stage.

| File | Size | Shows |
| --- | --- | --- |
| `library-{theme}.png` | 1440x876 | The whole window from its top edge: Library page, Up Next pane open, *Harbour at Six* paused at 1:00. Cut off below the player bar. |
| `albums-{theme}.png` | 1023x751 | The page area right of the navigation pane, Albums page, with the top of the player bar. |
| `lyrics-{theme}.png` | 1023x751 | The same area on the lyrics page of *Marigold Static*, *Sunlight folded in the wire* current. |
| `insights-{theme}.png` | 1023x751 | The same area on Insights, Last 30 Days. |
| `player-{1,2,3}-{theme}.png` | 1023x157 or 158 | The player bar alone, playing *Harbour at Six*, *Token Machine* and *Marigold Static*. |

The originals were lost, so these were cut from the README panels on `master` at 11086409 at their native resolution, inside the window edge so none of the old gradient stage came along: the window at (304, 772) in `hero-*.png`, the pages at (922, 100) and the player bars at (922, 148), (922, 370) and (922, 592) in `feature-*.png`.

Known difference: the app now draws the Up Next title in the normal text colour, but these screenshots predate that and show it in the accent green (`library-*`, and the `library` and `queue` banner scenes). Fix it at the next capture.

### Capturing them again (manual)

The app can't be driven from a script here, so this is done by hand. Replace a file with one of the same size and name, run both scripts, and look at every image.

1. **Never use the Store install** (package `48743Nagi.Nagi`) or its data. A development build has the same identity and would replace it and share its data. In an uncommitted change to `src/Nagi.WinUI/Package.appxmanifest`, give the `Identity` another `Name` (for example `Nagi.Capture`) and delete the `windows.fileTypeAssociation`, `windows.startupTask` and `windows.appExecutionAlias` extensions, so it claims neither the `nagi.exe` alias nor the file types. Build it with `dotnet build Nagi.sln -c Debug -p:Platform=x64` and register the build output's `AppxManifest.xml` with `Add-AppxPackage -Register`. Revert the manifest afterwards.
2. **An invented library, never a real one.** A folder of short generated audio files (any tone or silence) tagged with invented artists, albums and titles, each album with generated flat cover art (dots, rings, waves, a sunset, overlapping squares; no real brands or artwork). The current shots use: Ilse Varga Trio (*Still Water Sessions*, *After Hours at the Juniper*; songs *Harbour at Six* 2:30, *Slow Ferry*, *Last Tram Home*, *Still Water*, *A Room Facing North*, *Blue Kettle*, *Paper Boats*), The Glasswing Hours (*Marigold Static*, 4:59, with a synced `.lrc`: *Every signal is a gift*, *Sunlight folded in the wire*, *Soft as breath and twice as high*, *Stay a little, stay a while*), Northfield Static (*Afterimage*, *Signal Garden*, *Carrier Wave*), Mira Okonjo-Hale (*Paper Weather*, *Barometer*), Low Tide Arcade (*Token Machine*), Tomasz Belrose (*Letters to a Lighthouse*), and a few more to make 80 songs on 12 albums. Play songs for a few minutes each so Insights has history.
3. **Window and display.** Display scale 125% (what the current shots measure as), Mica backdrop, colours from the album art on. Size the window to 1440 physical pixels wide for `library-*`; the page and player shots are 1023 pixels wide. Capture light and dark with the app's theme setting, same state in both.
4. **Crop** each capture to the sizes above with no desktop or shadow around it, save over the old file, and run the scripts.
5. **Clean up**: `Get-AppxPackage Nagi.Capture | Remove-AppxPackage`, delete the invented library, and revert the manifest.
