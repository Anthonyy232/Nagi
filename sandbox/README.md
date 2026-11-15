# Nagi in Windows Sandbox

A disposable Windows to run a development build of Nagi in. A build has the same package identity as the Store install, so on the host it would replace it and share its data; in the sandbox it touches nothing of the host's. The run here installs the build, gives it an invented library, drives it with UI Automation and saves screenshots, which is also how `readme/inputs/` is made.

## Run

Windows 11 with Windows Sandbox turned on, the .NET SDK the Nagi checkout asks for, Node with this branch's `npm ci` done (for Playwright), and `ffmpeg` on `PATH`.

```powershell
powershell -ExecutionPolicy Bypass -File sandbox\run.ps1 <nagi-checkout> [-Inputs]
```

It takes about ten minutes, and the sandbox window has to stay unminimized while it runs (the script restores it behind your other windows if it is minimized; a minimized sandbox stops drawing and the app stalls). The screenshots land in `sandbox\.work\out\shots`, with `mini-player.png` and the app's log beside them. `-Inputs` also copies them over `readme\inputs\`; then run `node readme/build.cjs <nagi-checkout>` and look at every image.

Nothing is installed on the host. The only files written outside the sandbox are under `sandbox\.work\` (ignored by git): the test certificate, the demo library, the package and the results.

## What it does

| File | Where | What |
| --- | --- | --- |
| `run.ps1` | host | Makes the certificate and library if missing, builds the x64 package signed with the certificate, writes `.work\nagi.wsb`, starts the sandbox and waits for it. |
| `cert.ps1` | host | A self-signed code-signing certificate whose subject is the manifest's `Publisher`, written as files only (no certificate store). |
| `library.cjs` | host | The invented library: 80 songs on 12 albums of short quiet tones, tagged with made-up artists and titles, each album with generated flat cover art, and a synced `.lrc` for *Marigold Static*. |
| `logon.ps1` | sandbox | The sandbox's logon command: `setup.ps1`, then `capture.ps1`, then shuts the sandbox down. |
| `setup.ps1` | sandbox | Turns on sideloading, trusts the certificate, installs the package and copies the library to `C:\Music`. |
| `capture.ps1` | sandbox | Onboards with `C:\Music`, closes Nagi once the artwork has settled, writes a month of listening with `seed.ps1`, then for light and dark plays and pauses the same songs and saves each screenshot at the sizes in the media README. Ends with the mini-player. |
| `seed.ps1` | sandbox | Fills the listening history Insights reads, through the SQLite built into Windows, the same plays every run. |
| `onboard.ps1`, `ui.ps1` | sandbox | First-run setup through the folder picker, and the UI Automation, input and capture helpers. |

The package is the Release x64 build without `appsettings.json`, so no online keys go in, and the sandbox has no network. The app is self-contained, so the sandbox needs no Windows App SDK runtime or VC libraries. The sandbox shows Mica, light and dark and the app's colours as on a real PC; it runs at the host's display scale, which should be 125% for the screenshot sizes to match.
