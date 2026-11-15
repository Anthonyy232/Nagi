// Draws releases/<tag>.png for a release that has no art of its own.
// node banner.cjs <tag> "<label>" "<headline, *accent* word in asterisks>" <path to a master checkout>
// Needs Playwright with Chromium (NODE_PATH) and network access for the headline font.
const fs = require('fs');
const path = require('path');
const { chromium } = require('playwright');

const [tag, label, headline, repo] = process.argv.slice(2);
const uri = (file, type) => `data:${type};base64,${fs.readFileSync(path.join(repo, file)).toString('base64')}`;
const logo = uri('media/AppLogo.svg', 'image/svg+xml');
const shot = uri('.github/readme/hero-light.png', 'image/png');

const html = `<!doctype html><meta charset="utf-8">
<link href="https://fonts.googleapis.com/css2?family=Instrument+Serif:ital@0;1&display=swap" rel="stylesheet">
<style>
  * { margin: 0; box-sizing: border-box; }
  body { width: 1280px; height: 640px; overflow: hidden; background: #f4f5f1; color: #171a17; font-family: 'Segoe UI Variable Text', 'Segoe UI', system-ui, sans-serif; }
  .text { position: absolute; left: 72px; top: 0; bottom: 0; width: 470px; display: flex; flex-direction: column; justify-content: center; }
  .brand { display: flex; align-items: center; gap: 12px; font-size: 26px; font-weight: 600; }
  .brand img { width: 44px; height: 44px; }
  h1 { margin-top: 30px; font: 400 84px/1 'Instrument Serif', Georgia, serif; letter-spacing: -0.01em; text-wrap: balance; }
  h1 em { color: #23804f; }
  p { margin-top: 24px; font-size: 22px; color: #5b625c; }
  .stage { position: absolute; left: 590px; top: 56px; width: 760px; height: 640px; border-radius: 28px; overflow: hidden; background: linear-gradient(135deg, #c6edd3, #74c896); }
  /* the app window inside the README hero, which is 2048px wide */
  .stage div { position: absolute; left: 44px; top: 44px; width: 900px; height: 560px; border-radius: 8px; box-shadow: 0 24px 60px rgb(20 70 40 / .28);
    background: url(${shot}) -191px -482px / 1280px auto no-repeat; }
</style>
<div class="text">
  <div class="brand"><img src="${logo}" alt="">Nagi</div>
  <h1>${headline.replace(/\*(.+?)\*/g, '<em>$1</em>')}</h1>
  <p>${label}</p>
</div>
<div class="stage"><div></div></div>`;

(async () => {
  const browser = await chromium.launch();
  const page = await browser.newPage({ viewport: { width: 1280, height: 640 } });
  await page.setContent(html, { waitUntil: 'networkidle' });
  await page.evaluate(() => document.fonts.ready);
  await page.screenshot({ path: path.join(__dirname, `${tag}.png`) });
  await browser.close();
})();
