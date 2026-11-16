// node store/build.cjs <nagi> : the Microsoft Store logos and screenshots in store/
const fs = require('fs');
const path = require('path');
const { chromium } = require('playwright');
const { checkout, themes, dataUrl } = require('../common.cjs');

const logo = fs.readFileSync(path.join(checkout(), 'media', 'AppLogo.svg'), 'utf8').replace(/<title>.*?<\/title>/, '');
const t = themes.dark;
// Partner Center's names and sizes; the logo's share of the shorter side.
const logos = { 'poster-1440x2160': [1440, 2160, 0.5], 'box-2160x2160': [2160, 2160, 0.46], 'tile-300x300': [300, 300, 0.6], 'tile-150x150': [150, 150, 0.6], 'tile-71x71': [71, 71, 0.66] };
const page = (w, h, share) => `<!doctype html><meta charset="utf-8"><style>
  body { margin: 0; width: ${w}px; height: ${h}px; display: grid; place-items: center; background: radial-gradient(70% 55% at 50% 45%, ${t.stage} 0%, ${t.bg} 100%); }
  svg { width: ${Math.round(Math.min(w, h) * share)}px; }
</style>${logo}`;
// The README panels on a 16:9 page of their own background, so the listing matches the README and the site.
const shots = ['hero-dark', 'feature-library-dark', 'feature-lyrics-light', 'feature-color-dark', 'feature-insights-light'];

(async () => {
  const browser = await chromium.launch();
  for (const [name, [width, height, share]] of Object.entries(logos)) {
    const p = await browser.newPage({ viewport: { width, height } });
    await p.setContent(page(width, height, share));
    await p.screenshot({ path: path.join(__dirname, `${name}.png`) });
    await p.close();
  }
  for (const [i, name] of shots.entries()) {
    const p = await browser.newPage({ viewport: { width: 1920, height: 1080 } });
    const panel = dataUrl(path.join(checkout(), '.github', 'readme', `${name}.png`), 'image/png');
    await p.setContent(`<!doctype html><style>body { margin: 0; height: 1080px; display: grid; place-items: center; background: ${themes[name.split('-').pop()].bg}; } img { max-width: 1800px; max-height: 1040px; }</style><img src="${panel}">`);
    await p.screenshot({ path: path.join(__dirname, `screenshot-${i + 1}.png`) });
    await p.close();
  }
  await browser.close();
})();
