// Shared by readme/build.cjs and releases/banner.cjs.
const fs = require('fs');
const path = require('path');
const { chromium } = require('playwright');

const dataUrl = (file, type) => `data:${type};base64,${fs.readFileSync(file).toString('base64')}`;
const font = (style, file) => `@font-face { font-family: 'Instrument Serif'; font-style: ${style}; src: url(${dataUrl(path.join(__dirname, 'fonts', file), 'font/woff2')}); }`;

exports.dataUrl = dataUrl;

// The site's colours, with a flat stage for screenshots.
exports.themes = {
  light: { bg: '#f4f6f3', ink: '#171a18', muted: '#59615b', accent: '#26854d', stage: '#a9dfbd', edge: 'rgb(20 70 40 / .16)' },
  dark: { bg: '#131614', ink: '#e9ede9', muted: '#98a19a', accent: '#63c488', stage: '#1d4f31', edge: 'rgb(0 0 0 / .4)' },
};

// A screenshot from readme/inputs with its size in pixels.
exports.input = name => {
  const file = path.join(__dirname, 'readme', 'inputs', `${name}.png`);
  const png = fs.readFileSync(file);
  return { src: dataUrl(file, 'image/png'), width: png.readUInt32BE(16), height: png.readUInt32BE(20) };
};

exports.base = `<!doctype html><meta charset="utf-8">
<style>
  ${font('normal', 'InstrumentSerif-Regular.woff2')}
  ${font('italic', 'InstrumentSerif-Italic.woff2')}
  * { margin: 0; box-sizing: border-box; }
  body { overflow: hidden; font-family: 'Segoe UI Variable Text', 'Segoe UI', system-ui, sans-serif; }
  .serif { font-family: 'Instrument Serif', Georgia, serif; font-weight: 400; letter-spacing: -0.01em; }
</style>`;

// The Nagi checkout passed as the first argument: logos are read from it and images written into it.
exports.checkout = () => {
  const dir = process.argv[2];
  if (!dir || !fs.existsSync(path.join(dir, 'media', 'AppLogo.svg'))) throw new Error('Pass the path of a Nagi checkout first.');
  return path.resolve(dir);
};

// Opens a page with fonts and images loaded; the caller screenshots it.
exports.browser = () => chromium.launch();
exports.open = async (browser, html, width, height, scale = 1) => {
  const page = await browser.newPage({ viewport: { width, height }, deviceScaleFactor: scale });
  await page.setContent(html, { waitUntil: 'load' });
  await page.evaluate(() => Promise.all([...document.fonts].map(f => f.load())));
  return page;
};
