// node readme/build.cjs <nagi-checkout> : writes the README panels, the site images and the app icons into that checkout.
const fs = require('fs');
const path = require('path');
const { base, themes, input, dataUrl, checkout, browser, open } = require('../common.cjs');

const nagi = checkout();
const out = (...p) => path.join(nagi, ...p);
const logo = dataUrl(out('media', 'AppLogo.svg'), 'image/svg+xml');
const scale = 1.6;
// Drawn at 1.6x with CSS zoom rather than deviceScaleFactor, which resamples images by a pixel here and there.
// Positions are in image pixels so the screenshots land 1:1.
const px = n => `${n / scale}px`;

const page = (t, body, css, zoom = scale) => `${base}<style>
  body { background: transparent; color: ${t.ink}; }
  .card { zoom: ${zoom}; position: relative; overflow: hidden; border-radius: 28px; background: ${t.bg}; }
  .serif i { color: ${t.accent}; }
  .stage { position: absolute; overflow: hidden; background: ${t.stage}; }
  .shot { position: absolute; border-radius: 8px; outline: 1px solid ${t.edge}; }
  ${css}
</style>${body}`;

const shot = (name, x, y) => {
  const { src, width, height } = input(name);
  return `<img class="shot" src="${src}" style="left: ${px(x)}; top: ${px(y)}; width: ${px(width)}; height: ${px(height)}">`;
};

const hero = (t, theme, social) => page(t, `<div class="card">
  <div class="brand"><img src="${logo}" alt="">Nagi</div>
  <h1 class="serif">a <i>calm</i> home<br>for your music</h1>
  <p>A fast, private music player for your local library on Windows.</p>
  <div class="stage">${shot(`library-${theme}`, 144, 71)}</div>
</div>`, `
  .card { width: 1280px; height: ${social ? 640 : 1030}px; text-align: center; ${social ? 'border-radius: 0;' : ''} }
  .brand { display: inline-flex; align-items: center; gap: 10px; margin-top: ${social ? 30 : 48}px; font-size: 22px; font-weight: 600; }
  .brand img { width: 34px; height: 34px; }
  h1 { margin-top: ${social ? 12 : 26}px; font-size: ${social ? 92 : 108}px; line-height: 0.98; }
  p { margin-top: 23px; font-size: 20px; color: ${t.muted}; }
  .stage { left: 100px; right: 100px; top: ${social ? '362px' : px(701)}; bottom: 0; border-radius: 24px 24px 0 0; }`, social ? 1 : scale);

const features = [
  ['library', '01', 'Your folders,<br><i>your</i> library', 'Point Nagi at your music and it builds the library, watches for changes and plays almost any format. Your own Navidrome or Jellyfin server sits alongside.', theme => shot(`albums-${theme}`, 58, 55)],
  ['lyrics', '02', 'Lyrics, <i>in time</i>', 'Synced lyrics from tags, .lrc and .ttml files or optional online providers, highlighted as they are sung. Click a line to jump to it.', theme => shot(`lyrics-${theme}`, 58, 55)],
  ['color', '03', 'Color from <i>the cover</i>', 'The player and accents follow the album that is playing, on Mica, Mica Alt or Acrylic, in light and dark.', theme => [103, 325, 547].map((y, i) => shot(`player-${i + 1}-${theme}`, 58, y)).join('')],
  ['insights', '04', 'Every play,<br><i>remembered</i>', 'Listening history and Insights are kept on your PC. Nagi stays offline unless you add a server or turn on an online service.', theme => shot(`insights-${theme}`, 58, 55)],
];

const feature = (t, theme, [, num, title, text, stage]) => page(t, `<div class="card">
  <div class="copy"><div class="num">${num}</div><h2 class="serif">${title}</h2><p>${text}</p></div>
  <div class="stage">${stage(theme)}</div>
</div>`, `
  .card { width: 1280px; height: 560px; }
  .copy { position: absolute; left: 64px; width: 436px; top: 0; bottom: 0; display: flex; flex-direction: column; justify-content: center; }
  .num { font: 600 15px ui-monospace, 'Cascadia Code', Consolas, monospace; color: ${t.muted}; }
  h2 { margin-top: 13.5px; font-size: 56px; line-height: 1.04; }
  p { margin-top: 18.75px; font-size: 18px; line-height: 1.5; color: ${t.muted}; }
  .stage { left: 540px; top: ${px(45)}; right: 28px; bottom: 28px; border-radius: 20px; }`);

// Encodes a PNG screenshot region as WebP in the page's own canvas.
const webp = (page, png, [x, y, w, h], file) => page.evaluate(async ({ src, x, y, w, h }) => {
  const img = new Image();
  img.src = src;
  await img.decode();
  const canvas = Object.assign(document.createElement('canvas'), { width: w, height: h });
  canvas.getContext('2d').drawImage(img, x, y, w, h, 0, 0, w, h);
  return canvas.toDataURL('image/webp', 0.9).split(',')[1];
}, { src: `data:image/png;base64,${png.toString('base64')}`, x, y, w, h }).then(b64 => fs.writeFileSync(file, Buffer.from(b64, 'base64')));

// MSIX assets: [file, width, height, logo size as a share of the shorter side].
const icons = [['AppLogo.png', 1024, 1024, 1]];
const scaled = (name, w, h, share) => [100, 125, 150, 200, 400].forEach(s => icons.push([`${name}.scale-${s}.png`, Math.round(w * s / 100), Math.round(h * s / 100), share]));
scaled('LargeTile', 310, 310, 1 / 3);
scaled('Square150x150Logo', 150, 150, 1 / 3);
scaled('Wide310x150Logo', 310, 150, 1 / 3);
scaled('SplashScreen', 620, 300, 1 / 3);
scaled('SmallTile', 71, 71, 1 / 2);
scaled('Square44x44Logo', 44, 44, 3 / 4);
scaled('StoreLogo', 50, 50, 1);
for (const t of [16, 24, 32, 48, 256]) {
  icons.push([`Square44x44Logo.targetsize-${t}.png`, t, t, 3 / 4]);
  icons.push([`Square44x44Logo.altform-unplated_targetsize-${t}.png`, t, t, 1]);
  icons.push([`Square44x44Logo.altform-lightunplated_targetsize-${t}.png`, t, t, 1]);
}
const svg = name => fs.readFileSync(out('media', name), 'utf8').replace(/<title>.*?<\/title>/, '');
const marks = { large: svg('AppLogo.svg'), small: svg('AppLogo-small.svg') };

// PNG-compressed entries; a size of 256 is stored as 0.
const ico = pngs => {
  const head = Buffer.alloc(6 + 16 * pngs.length);
  head.writeUInt16LE(1, 2);
  head.writeUInt16LE(pngs.length, 4);
  let offset = head.length;
  pngs.forEach(([size, png], i) => {
    const entry = 6 + 16 * i;
    head.writeUInt8(size % 256, entry);
    head.writeUInt8(size % 256, entry + 1);
    head.writeUInt16LE(1, entry + 4);
    head.writeUInt16LE(32, entry + 6);
    head.writeUInt32LE(png.length, entry + 8);
    head.writeUInt32LE(offset, entry + 12);
    offset += png.length;
  });
  return Buffer.concat([head, ...pngs.map(([, png]) => png)]);
};

(async () => {
  const b = await browser();
  for (const [theme, t] of Object.entries(themes)) {
    let p = await open(b, hero(t, theme), 2048, 1648);
    fs.writeFileSync(out('.github', 'readme', `hero-${theme}.png`), await p.screenshot({ omitBackground: true }));
    await webp(p, await p.screenshot(), [160, 701, 1728, 947], out('site', 'img', `hero-${theme}.webp`));
    await p.close();
    for (const f of features) {
      p = await open(b, feature(t, theme, f), 2048, 896);
      fs.writeFileSync(out('.github', 'readme', `feature-${f[0]}-${theme}.png`), await p.screenshot({ omitBackground: true }));
      await webp(p, await p.screenshot(), [864, 45, 1139, 807], out('site', 'img', `${f[0]}-${theme}.webp`));
      await p.close();
    }
  }
  const p = await open(b, hero(themes.light, 'light', true), 1280, 640);
  await p.screenshot({ path: out('site', 'img', 'social.png') });
  await p.close();

  const unplated = [];
  for (const [file, w, h, share] of icons) {
    const size = Math.min(w, h) * share;
    const page = await b.newPage({ viewport: { width: w, height: h } });
    // below 40px the large mark's inner shapes blur together, so the small cut is drawn
    await page.setContent(`<style>* { margin: 0; } svg { position: absolute; left: ${(w - size) / 2}px; top: ${(h - size) / 2}px; width: ${size}px; height: ${size}px; }</style>${size < 40 ? marks.small : marks.large}`);
    const png = await page.screenshot({ omitBackground: true });
    fs.writeFileSync(out('src', 'Nagi.WinUI', 'Assets', file), png);
    if (file.includes('altform-unplated')) unplated.push([w, png]);
    await page.close();
  }
  fs.writeFileSync(out('src', 'Nagi.WinUI', 'Assets', 'AppLogo.ico'), ico(unplated));
  await b.close();
})();
