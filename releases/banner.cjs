// node releases/banner.cjs <nagi-checkout> [<tag> "<highlight, *accent* in asterisks>" [scene]] : release banners, 1280x640 at 2x, beside this file. Steps in the README.
// With a tag it records the banner in banners.json and draws it; without, it redraws them all.
const fs = require('fs');
const path = require('path');
const { base, themes, input, dataUrl, checkout, browser, open } = require('../common.cjs');

const file = path.join(__dirname, 'banners.json');
const banners = JSON.parse(fs.readFileSync(file, 'utf8'));
const logo = dataUrl(path.join(checkout(), 'media', 'AppLogo.svg'), 'image/svg+xml');
const [tag, headline, scene] = process.argv.slice(3);
if (tag) {
  banners[tag] = { headline, scene: scene || 'mark' };
  fs.writeFileSync(file, JSON.stringify(banners, null, 2) + '\n');
}

// Cards cut from the screenshots in readme/inputs: [input, x, y, width, height] in image pixels, inside the window so no frame comes along.
const page = name => [[name, 2, 2, 1017, 614]];
const scenes = {
  library: [['library-light', 3, 1, 1433, 710]],
  queue: [['library-light', 1022, 59, 399, 643]],
  albums: page('albums-light'),
  lyrics: page('lyrics-light'),
  insights: page('insights-light'),
  player: [[1, 1], [2, 1], [3, 2]].map(([n, y]) => [`player-${n}-light`, 2, y, 1018, 153]),
};
const stage = name => {
  if (name === 'mark') return `<div class="stage"><img class="mark" src="${logo}" alt=""></div>`;
  const cards = scenes[name];
  const height = cards.reduce((sum, c) => sum + c[4], 0);
  const scale = Math.min(564 / cards[0][3], (504 - 28 * (cards.length - 1)) / height);
  return `<div class="stage">${cards.map(([name, x, y, w, h]) => {
    const shot = input(name);
    return `<div class="card" style="width: ${w * scale}px; height: ${h * scale}px; background: url(${shot.src}) ${-x * scale}px ${-y * scale}px / ${shot.width * scale}px auto no-repeat"></div>`;
  }).join('')}</div>`;
};

const html = (label, text, scene) => `${base}<style>
  body { width: 1280px; height: 640px; background: #f4f5f1; color: #171a17; }
  .copy { position: absolute; left: 72px; top: 64px; bottom: 68px; width: 500px; display: flex; flex-direction: column; }
  .brand { display: flex; align-items: center; gap: 12px; font-size: 26px; font-weight: 600; }
  .brand img { width: 44px; height: 44px; }
  .label { margin-top: auto; font: 500 18px ui-monospace, 'Cascadia Code', Consolas, monospace; color: #5b625c; }
  h1 { margin-top: 14px; font-size: 92px; line-height: 1; text-wrap: balance; }
  h1 em { color: ${themes.light.accent}; }
  .stage { position: absolute; left: 620px; top: 40px; width: 620px; height: 560px; border-radius: 28px; background: ${themes.light.stage}; display: flex; flex-direction: column; align-items: center; justify-content: center; gap: 28px; }
  .card { border-radius: 10px; outline: 1px solid ${themes.light.edge}; }
  .mark { width: 280px; height: 280px; }
</style>
<div class="copy">
  <div class="brand"><img src="${logo}" alt="">Nagi</div>
  <div class="label">${label}</div>
  <h1 class="serif">${text.replace(/\*(.+?)\*/g, '<em>$1</em>')}</h1>
</div>
${stage(scene)}`;

(async () => {
  const b = await browser();
  for (const [t, banner] of tag ? [[tag, banners[tag]]] : Object.entries(banners)) {
    const p = await open(b, html(banner.label || `Release ${t}`, banner.headline, banner.scene), 1280, 640, 2);
    // a long highlight shrinks until it fits three lines' worth of space
    await p.evaluate(() => {
      const h1 = document.querySelector('h1');
      for (let size = 92; size > 48 && (h1.offsetHeight > 290 || h1.scrollWidth > h1.clientWidth); size -= 4) h1.style.fontSize = `${size}px`;
    });
    await p.screenshot({ path: path.join(__dirname, `${t}.png`) });
    await p.close();
  }
  await b.close();
})();
