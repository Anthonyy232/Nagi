# Nagi media

Images that Nagi's releases and issues link to, kept off `master` so it stays light.

## Release banners

`releases/banner.cjs` draws every banner from `releases/banners.json`. It needs Node, Playwright with Chromium on `NODE_PATH`, and network access for the headline font. It reads the logo and screenshots from `master`.

1. Check this branch out beside the repository: `git worktree add <dir> media`.
2. Add a release: `node <dir>/releases/banner.cjs X.Y.Z "Short *highlight*" [scene]`. The words between asterisks take the accent colour. The scenes are listed in the script; without one the banner shows the logo.
3. Look at `releases/X.Y.Z.png`, then commit and push `media`.
4. Open the release notes with `![Nagi X.Y.Z](https://raw.githubusercontent.com/Anthonyy232/Nagi/media/releases/X.Y.Z.png)`.

`node <dir>/releases/banner.cjs` with no arguments redraws them all, after a change to the script or the screenshots.
