// node sandbox/library.cjs <out-dir> : writes the invented demo library (tagged MP3s with generated cover art, one synced .lrc).
// Every artist, album, title and cover here is made up. Needs ffmpeg on PATH.
const fs = require('fs');
const path = require('path');
const { execFileSync } = require('child_process');
const { chromium } = require('playwright');

const out = path.resolve(process.argv[2] || path.join(__dirname, '.work', 'library'));

const t = s => { const [m, sec] = s.split(':').map(Number); return m * 60 + sec; };

// [artist, album, year, genre, cover, songs as [title, m:ss]]
const albums = [
  ['Ilse Varga Trio', 'Still Water Sessions', 2021, 'Jazz', 'rings:#1f6f5c:#e2b443', [
    ['Harbour at Six', '2:30'], ['Blue Kettle', '3:19'], ['Paper Boats', '3:25'], ['Still Water', '4:33'],
    ['Slow Ferry', '5:06'], ['Last Tram Home', '3:58'], ['A Room Facing North', '4:12']]],
  ['Ilse Varga Trio', 'After Hours at the Juniper', 2024, 'Jazz', 'sunset:#7a1f3d:#f4b393', [
    ['Brushed Snare Waltz', '3:44'], ['Juniper Lane', '4:02'], ['Closing Time Ballad', '5:21'], ['Velvet Stairs', '3:13'],
    ['Midnight Ledger', '4:47'], ['Two Coats, One Hook', '3:36']]],
  ['The Glasswing Hours', 'Marigold Static', 2020, 'Indie Pop', 'dots:#eaa823:#fff3c4', [
    ['Afterglow Avenue', '3:41'], ['Marigold Static', '4:59'], ['Every Signal', '3:22'], ['Wire and Wren', '3:57'],
    ['Paper Lanterns', '4:15'], ['Second Summer', '3:08'], ['Glasswing', '4:26'], ['Soft Machinery', '3:49']]],
  ['Northfield Static', 'Afterimage', 2020, 'Electronic', 'rings:#0f8b8d:#bff3ee', [
    ['Afterimage', '4:20'], ['Signal Garden', '5:02'], ['Carrier Wave', '4:38'], ['Low Orbit', '3:55'],
    ['Cold Start', '4:11'], ['Phosphor', '5:24'], ['Northfield', '6:03']]],
  ['Mira Okonjo-Hale', 'Paper Weather', 2021, 'Folk', 'squares:#b9b3e3:#ffffff:#5a4f8c', [
    ['Paper Weather', '3:33'], ['Barometer', '4:04'], ['Kite Season', '3:17'], ['The Long Field', '4:42'],
    ['Rain Delay', '3:29'], ['Wool and Wire', '3:51'], ['Lantern Hour', '4:19']]],
  ['Low Tide Arcade', 'Token Machine', 2022, 'Synthwave', 'grid:#e0458f:#ffd166', [
    ['Token Machine', '3:52'], ['High Score', '4:08'], ['Neon Pier', '4:31'], ['Insert Coin', '2:58'],
    ['Boardwalk Glow', '5:10'], ['Continue?', '3:24'], ['Last Life', '4:46']]],
  ['Tomasz Belrose', 'Letters to a Lighthouse', 2019, 'Classical', 'waves:#2e7fb8:#a7c8de:#3c5f7d', [
    ['Letters to a Lighthouse', '6:12'], ['Keeper', '4:55'], ['Fog Signal', '5:37'], ['Tidewater', '4:23'],
    ['The Lamp Room', '7:01'], ['Morning Watch', '5:14']]],
  ['Quiet Harbour Ensemble', 'Field Notes, Vol. 2', 2025, 'Ambient', 'squares:#6d9a7a:#eef5ee:#2f4a37', [
    ['Borrowed Light', '5:45'], ['Moss Study', '6:20'], ['Low Clouds', '4:58'], ['Field Recording No. 4', '7:12'],
    ['Slow Thaw', '5:33'], ['Evening Survey', '6:07']]],
  ['Saffron Relay', 'Overpass', 2023, 'Rock', 'stripes:#d9532c:#ffe0c2', [
    ['Overpass', '3:46'], ['Brake Lights', '3:12'], ['Ninety Miles', '4:05'], ['Static on the Line', '3:34'],
    ['Exit Ramp', '2:59'], ['Long Haul', '4:28'], ['Gasoline Sunrise', '3:53'], ['Tailwind', '3:21']]],
  ['Juno Pell', 'Small Hours', 2022, 'R&B', 'dots:#3b2f6b:#c9b8ff', [
    ['Small Hours', '3:48'], ['Porch Light', '3:26'], ['Keep the Change', '4:02'], ['Velvet Static', '3:39'],
    ['Slow Dial', '4:14'], ['Window Seat', '3:31']]],
  ['Cedar & Coil', 'Ferrous', 2018, 'Post-Rock', 'waves:#4a4a45:#c8c2b0:#8b8576', [
    ['Ferrous', '7:44'], ['Iron Bloom', '6:31'], ['Rust Belt Hymn', '8:02'], ['Anvil', '5:49'],
    ['Slag', '6:55']]],
  ['Opal Vance', 'Greenhouse', 2024, 'Pop', 'sunset:#2f8f5b:#fbe38e', [
    ['Greenhouse', '3:18'], ['Fern', '2:54'], ['Glass Roof', '3:27'], ['Humid', '3:09'],
    ['Seedling', '3:42'], ['Overgrown', '3:36'], ['Bloom', '3:05']]],
];

const lrc = [
  ['0:00.00', ''], ['0:12.00', 'Every signal is a gift'], ['0:18.50', 'Every static hum a song'],
  ['0:25.00', 'Sunlight folded in the wire'], ['0:31.50', 'Soft as breath and twice as high'],
  ['0:38.00', 'Stay a little, stay a while'], ['0:44.50', 'Marigold on the dial'],
  ['0:51.00', 'Turn it low and let it ring'], ['0:57.50', 'Every signal is a gift'],
  ['1:04.00', 'Hold the line until it sings'], ['1:10.50', 'Sunlight folded in the wire'],
  ['1:17.00', 'Stay a little, stay a while'],
];

const cover = (spec, size = 600) => {
  const [kind, a, b, c] = spec.split(':');
  const s = size;
  let body = '';
  if (kind === 'rings') for (let i = 0; i < 6; i++) body += `<circle cx="${s / 2}" cy="${s / 2}" r="${s * (0.07 + i * 0.08)}" fill="none" stroke="${b}" stroke-width="${s * 0.012}" opacity="${1 - i * 0.12}"/>`;
  if (kind === 'dots') for (let y = 0; y < 7; y++) for (let x = 0; x < 7; x++) {
    const r = s * (0.018 + ((x * 7 + y * 3) % 5) * 0.008);
    body += `<circle cx="${s * (0.14 + x * 0.12)}" cy="${s * (0.14 + y * 0.12)}" r="${r}" fill="${(x + 2 * y) % 6 ? b : '#00000055'}"/>`;
  }
  if (kind === 'sunset') {
    body += `<circle cx="${s / 2}" cy="${s * 0.56}" r="${s * 0.26}" fill="${b}"/><rect y="${s * 0.56}" width="${s}" height="${s * 0.44}" fill="#00000066"/>`;
    for (let i = 0; i < 5; i++) body += `<rect x="${s * (0.24 + i * 0.03)}" y="${s * (0.6 + i * 0.05)}" width="${s * (0.52 - i * 0.06)}" height="${s * 0.015}" rx="${s * 0.007}" fill="${b}" opacity="${0.8 - i * 0.13}"/>`;
  }
  if (kind === 'squares') body += `<rect x="${s * 0.15}" y="${s * 0.15}" width="${s * 0.42}" height="${s * 0.42}" rx="${s * 0.04}" fill="${b}"/><circle cx="${s * 0.66}" cy="${s * 0.32}" r="${s * 0.12}" fill="${c}" opacity="0.7"/><rect x="${s * 0.43}" y="${s * 0.43}" width="${s * 0.42}" height="${s * 0.42}" rx="${s * 0.04}" fill="${c}"/>`;
  if (kind === 'grid') {
    body += `<circle cx="${s / 2}" cy="${s * 0.42}" r="${s * 0.2}" fill="${b}"/>`;
    for (let i = 0; i <= 10; i++) body += `<line x1="${s / 2}" y1="${s * 0.6}" x2="${s * (i / 10) * 1.6 - s * 0.3}" y2="${s}" stroke="${b}" stroke-width="2"/>`;
    for (let i = 0; i < 5; i++) body += `<line x1="0" x2="${s}" y1="${s * (0.62 + i * i * 0.025)}" y2="${s * (0.62 + i * i * 0.025)}" stroke="${b}" stroke-width="2"/>`;
  }
  if (kind === 'waves') for (let i = 0; i < 5; i++) {
    const y = s * (0.38 + i * 0.12);
    body += `<path d="M0 ${y} Q ${s * 0.25} ${y - s * 0.07} ${s * 0.5} ${y} T ${s} ${y} V ${s} H 0 Z" fill="${i % 2 ? c : b}" opacity="${0.55 + i * 0.1}"/>`;
  }
  if (kind === 'stripes') for (let i = 0; i < 9; i++) body += `<rect x="${-s * 0.2 + i * s * 0.16}" y="-${s * 0.2}" width="${s * 0.06}" height="${s * 1.4}" fill="${b}" transform="rotate(30 ${s / 2} ${s / 2})" opacity="${0.4 + (i % 3) * 0.2}"/>`;
  return `<svg xmlns="http://www.w3.org/2000/svg" width="${s}" height="${s}"><rect width="${s}" height="${s}" fill="${a}"/>${body}</svg>`;
};

(async () => {
  fs.rmSync(out, { recursive: true, force: true });
  const browser = await chromium.launch();
  const page = await browser.newPage({ viewport: { width: 600, height: 600 } });
  let count = 0;
  for (const [artist, album, year, genre, art, songs] of albums) {
    const dir = path.join(out, artist, album);
    fs.mkdirSync(dir, { recursive: true });
    await page.setContent(`<style>*{margin:0}</style>${cover(art)}`);
    const jpg = path.join(dir, 'cover.jpg');
    await page.screenshot({ path: jpg, type: 'jpeg', quality: 90 });
    for (const [i, [title, length]] of songs.entries()) {
      const file = path.join(dir, `${String(i + 1).padStart(2, '0')} ${title.replace(/[?]/g, '')}.mp3`);
      // a quiet tone per album so playback can be heard in the sandbox
      const tone = 196 + 22 * albums.findIndex(a => a[1] === album);
      execFileSync('ffmpeg', ['-v', 'error', '-y',
        '-f', 'lavfi', '-i', `sine=f=${tone}:d=${t(length)}:sample_rate=22050`, '-i', jpg,
        '-map', '0:a', '-map', '1:v', '-af', 'volume=0.05', '-c:a', 'libmp3lame', '-b:a', '32k', '-ac', '1',
        '-c:v', 'copy', '-disposition:v', 'attached_pic', '-id3v2_version', '3',
        '-metadata', `title=${title}`, '-metadata', `artist=${artist}`, '-metadata', `album_artist=${artist}`,
        '-metadata', `album=${album}`, '-metadata', `date=${year}`, '-metadata', `genre=${genre}`,
        '-metadata', `track=${i + 1}/${songs.length}`, file]);
      if (title === 'Marigold Static') fs.writeFileSync(file.replace(/\.mp3$/, '.lrc'), lrc.map(([at, line]) => `[${at.padStart(8, '0')}]${line}`).join('\n') + '\n');
      count++;
    }
    fs.rmSync(jpg);
  }
  await browser.close();
  console.log(`${count} songs on ${albums.length} albums in ${out}`);
})();
