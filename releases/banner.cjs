// node banner.cjs [<tag> "<highlight, *accent* in asterisks>" [scene]] : 1280x640 release banners beside this file. Steps in the README.
// With arguments it records the banner in banners.json and draws it; without, it redraws them all.
// Run from a worktree of the Nagi repo: the logo and screenshots are read from master.
// Needs Playwright with Chromium (NODE_PATH) and network access for the headline font.
const fs = require('fs');
const path = require('path');
const { execFileSync } = require('child_process');
const { chromium } = require('playwright');

const file = path.join(__dirname, 'banners.json');
const banners = JSON.parse(fs.readFileSync(file, 'utf8'));
const [tag, headline, scene] = process.argv.slice(2);
if (tag) {
  banners[tag] = { headline, scene: scene || 'mark' };
  fs.writeFileSync(file, JSON.stringify(banners, null, 2) + '\n');
}

const asset = (name, type) => `data:${type};base64,${execFileSync('git', ['show', `master:${name}`], { cwd: __dirname, maxBuffer: 1 << 26 }).toString('base64')}`;
const logo = asset('media/AppLogo.svg', 'image/svg+xml');

// Cards cut from the README images: [image, x, y, width, height] in image pixels, inside the window so no backdrop comes along.
const page = image => [[image, 924, 102, 1017, 614]];
const scenes = {
  library: [['hero-light', 307, 773, 1433, 710]],
  queue: [['hero-light', 1326, 831, 399, 643]],
  albums: page('feature-library-light'),
  lyrics: page('feature-lyrics-light'),
  insights: page('feature-insights-light'),
  player: [149, 371, 594].map(y => ['feature-color-light', 924, y, 1018, 153]),
};
const shots = {};
const stage = name => {
  if (name === 'mark') return `<div class="stage"><img class="mark" src="${logo}" alt=""></div>`;
  const cards = scenes[name];
  const height = cards.reduce((sum, c) => sum + c[4], 0);
  const scale = Math.min(564 / cards[0][3], (504 - 28 * (cards.length - 1)) / height);
  return `<div class="stage">${cards.map(([image, x, y, w, h]) => {
    shots[image] ??= asset(`.github/readme/${image}.png`, 'image/png');
    return `<div class="card" style="width: ${w * scale}px; height: ${h * scale}px; background: url(${shots[image]}) ${-x * scale}px ${-y * scale}px / ${2048 * scale}px auto no-repeat"></div>`;
  }).join('')}</div>`;
};

const html = (label, text, scene) => `<!doctype html><meta charset="utf-8">
<link href="https://fonts.googleapis.com/css2?family=Instrument+Serif:ital@0;1&display=swap" rel="stylesheet">
<style>
  * { margin: 0; box-sizing: border-box; }
  body { width: 1280px; height: 640px; overflow: hidden; background: #f4f5f1; color: #171a17; font-family: 'Segoe UI Variable Text', 'Segoe UI', system-ui, sans-serif; }
  .copy { position: absolute; left: 72px; top: 64px; bottom: 68px; width: 500px; display: flex; flex-direction: column; }
  .brand { display: flex; align-items: center; gap: 12px; font-size: 26px; font-weight: 600; }
  .brand img { width: 44px; height: 44px; }
  .label { margin-top: auto; font: 500 18px ui-monospace, 'Cascadia Code', Consolas, monospace; color: #5b625c; }
  h1 { margin-top: 14px; font: 400 92px/1 'Instrument Serif', Georgia, serif; letter-spacing: -0.01em; text-wrap: balance; }
  h1 em { color: #26854d; }
  .stage { position: absolute; left: 620px; top: 40px; width: 620px; height: 560px; border-radius: 28px; background: #a9dfbd; display: flex; flex-direction: column; align-items: center; justify-content: center; gap: 28px; }
  .card { border-radius: 10px; outline: 1px solid rgb(20 70 40 / .16); }
  .mark { width: 280px; height: 280px; }
</style>
<div class="copy">
  <div class="brand"><img src="${logo}" alt="">Nagi</div>
  <div class="label">${label}</div>
  <h1>${text.replace(/\*(.+?)\*/g, '<em>$1</em>')}</h1>
</div>
${stage(scene)}`;

(async () => {
  const browser = await chromium.launch();
  for (const [t, b] of tag ? [[tag, banners[tag]]] : Object.entries(banners)) {
    const page = await browser.newPage({ viewport: { width: 1280, height: 640 } });
    await page.setContent(html(b.label || `Release ${t}`, b.headline, b.scene), { waitUntil: 'networkidle' });
    await page.evaluate(() => document.fonts.ready);
    if (!await page.evaluate(() => document.fonts.check("92px 'Instrument Serif'"))) throw new Error('Instrument Serif did not load');
    // a long highlight shrinks until it fits three lines' worth of space
    await page.evaluate(() => {
      const h1 = document.querySelector('h1');
      for (let size = 92; size > 48 && (h1.offsetHeight > 290 || h1.scrollWidth > h1.clientWidth); size -= 4) h1.style.fontSize = `${size}px`;
    });
    await page.screenshot({ path: path.join(__dirname, `${t}.png`) });
    await page.close();
  }
  await browser.close();
})();
