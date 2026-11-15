// node banner.cjs [<tag> "<highlight, *accent* in asterisks>" [scene]] : 1280x640 release banners beside this file.
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

// README image, the scale to draw it at, and the point of it that sits at the stage's top-left corner.
const scenes = {
  library: ['hero-light', 0.625, 168, 706],
  queue: ['hero-light', 1.1, 1284, 716],
  albums: ['feature-library-light', 0.7, 870, 50],
  lyrics: ['feature-lyrics-light', 0.7, 870, 50],
  insights: ['feature-insights-light', 0.7, 870, 50],
  player: ['feature-color-light', 0.7, 870, 50],
};
const shots = {};
const stage = name => {
  if (name === 'mark') return `<div class="stage mark"><img src="${logo}" alt=""></div>`;
  const [image, scale, x, y] = scenes[name];
  shots[image] ??= asset(`.github/readme/${image}.png`, 'image/png');
  return `<div class="stage" style="background: url(${shots[image]}) ${-x * scale}px ${-y * scale}px / ${2048 * scale}px auto no-repeat"></div>`;
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
  .stage { position: absolute; left: 620px; top: 40px; width: 620px; height: 560px; border-radius: 28px; }
  .mark { display: grid; place-items: center; background: radial-gradient(80% 90% at 8% 0%, #c9efd6 0%, transparent 70%), linear-gradient(135deg, #aee2c1, #6fc592); }
  .mark img { width: 280px; height: 280px; filter: drop-shadow(0 18px 30px rgb(20 70 40 / .14)); }
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
