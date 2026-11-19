# Nagi media

The images Nagi's README, website, app package and releases use, and the scripts that draw them. Kept off `master` so it stays light.

| Path | What it is |
| --- | --- |
| `readme/build.cjs` | Draws the README panels, the site images and the app icon set into a Nagi checkout. |
| `readme/inputs/` | The app screenshots the panels and banners are made from. |
| `releases/banner.cjs`, `releases/banners.json` | Draws the release banners, 1280x640 at 2x, `releases/<tag>.png`. |
| `store/build.cjs` | Draws the Microsoft Store logos and the five listing screenshots into `store/`: `node store/build.cjs <nagi>`. They are uploaded to Partner Center by hand. |
| `common.cjs` | Fonts, colours and page helpers both scripts share. |
| `fonts/` | Instrument Serif, the headline font, under the SIL Open Font License (`fonts/OFL.txt`). |
| `issues/` | Evidence images linked from bug reports. |
| `sandbox/` | Runs a development build in Windows Sandbox and captures `readme/inputs/`. |

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
| `player-{1,2,3}-{theme}.png` | 1023x157 | The player bar alone, paused on *Harbour at Six*, *Token Machine* and *Marigold Static*. |

They are captured from a Release build running in Windows Sandbox on an invented library, by the script in `sandbox/`; its README has the details.

```powershell
powershell -ExecutionPolicy Bypass -File sandbox\run.ps1 <nagi> -Inputs
node readme/build.cjs <nagi>
```

Look at every input and every rebuilt image, then commit the inputs here and the images on `master`. The sizes above assume the host display is at 125%.
