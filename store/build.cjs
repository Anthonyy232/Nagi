// node store/build.cjs <nagi> : the Microsoft Store logos and screenshots in store/
const fs = require('fs');
const path = require('path');
const { chromium } = require('playwright');
const { checkout, themes, input } = require('../common.cjs');

const logo = fs.readFileSync(path.join(checkout(), 'media', 'AppLogo.svg'), 'utf8').replace(/<title>.*?<\/title>/, '');
const t = themes.dark;
// Partner Center's names and sizes; the logo's share of the shorter side.
const logos = { 'poster-1440x2160': [1440, 2160, 0.5], 'box-2160x2160': [2160, 2160, 0.46], 'tile-300x300': [300, 300, 0.6], 'tile-150x150': [150, 150, 0.6], 'tile-71x71': [71, 71, 0.66] };
const page = (w, h, share) => `<!doctype html><meta charset="utf-8"><style>
  body { margin: 0; width: ${w}px; height: ${h}px; display: grid; place-items: center; background: radial-gradient(70% 55% at 50% 45%, ${t.stage} 0%, ${t.bg} 100%); }
  svg { width: ${Math.round(Math.min(w, h) * share)}px; }
</style>${logo}`;
// Real captures of the app on its demo library. The Store wants at least 1366x768, so a smaller window sits on a stage.
const shots = ['library-dark', 'albums-light', 'lyrics-dark', 'insights-light', 'library-light'];

(async () => {
  const browser = await chromium.launch();
  for (const [name, [width, height, share]] of Object.entries(logos)) {
    const p = await browser.newPage({ viewport: { width, height } });
    await p.setContent(page(width, height, share));
    await p.screenshot({ path: path.join(__dirname, `${name}.png`) });
    await p.close();
  }
  for (const [i, name] of shots.entries()) {
    const shot = input(name);
    const stage = themes[name.endsWith('dark') ? 'dark' : 'light'].stage;
    const p = await browser.newPage({ viewport: { width: 1440, height: 876 } });
    await p.setContent(`<!doctype html><style>body { margin: 0; height: 876px; display: grid; place-items: center; background: ${stage}; } img { border-radius: ${shot.width < 1366 ? 8 : 0}px; box-shadow: 0 30px 80px -30px #0008; }</style><img src="${shot.src}">`);
    await p.screenshot({ path: path.join(__dirname, `screenshot-${i + 1}.png`) });
    await p.close();
  }
  await browser.close();
})();
