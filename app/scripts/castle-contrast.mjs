// Prints the castle palette contrast table (#361): text at its worst-case pixel on
// the textured fills, graphics at their closest pixel. Run by hand for the PR;
// castleContrast.test.ts enforces the same pairs in CI. Exits non-zero on failure.
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { inflateSync } from 'node:zlib';

const root = join(dirname(fileURLToPath(import.meta.url)), '..');
const tokens = JSON.parse(readFileSync(join(root, 'src/theme/tokens.json'), 'utf8'));
const castle = tokens.castle;
const night = tokens.castleDark;

// Minimal PNG decode: 8-bit RGB/RGBA, non-interlaced, which is what castle-textures.py writes.
function pixelsOf(file) {
  const raw = readFileSync(join(root, 'assets/castle', file));
  let pos = 8;
  let width = 0;
  let height = 0;
  let bpp = 0;
  const idat = [];
  while (pos < raw.length) {
    const len = raw.readUInt32BE(pos);
    const type = raw.subarray(pos + 4, pos + 8).toString('ascii');
    const data = raw.subarray(pos + 8, pos + 8 + len);
    if (type === 'IHDR') {
      width = data.readUInt32BE(0);
      height = data.readUInt32BE(4);
      if (data[8] !== 8 || (data[9] !== 2 && data[9] !== 6) || data[12] !== 0) throw new Error(`${file}: unsupported PNG form`);
      bpp = data[9] === 2 ? 3 : 4;
    } else if (type === 'IDAT') {
      idat.push(data);
    }
    pos += 12 + len;
  }
  const scan = inflateSync(Buffer.concat(idat));
  const stride = width * bpp;
  const out = [];
  let prev = Buffer.alloc(stride);
  for (let y = 0; y < height; y++) {
    const filter = scan[y * (stride + 1)];
    const row = Buffer.from(scan.subarray(y * (stride + 1) + 1, (y + 1) * (stride + 1)));
    for (let i = 0; i < stride; i++) {
      const a = i >= bpp ? row[i - bpp] : 0;
      const b = prev[i];
      const c = i >= bpp ? prev[i - bpp] : 0;
      const pred = filter === 1 ? a : filter === 2 ? b : filter === 3 ? (a + b) >> 1 : filter === 4 ? paeth(a, b, c) : 0;
      row[i] = (row[i] + pred) & 255;
    }
    for (let x = 0; x < width; x++) out.push([row[x * bpp], row[x * bpp + 1], row[x * bpp + 2]]);
    prev = row;
  }
  return out;
}

function paeth(a, b, c) {
  const p = a + b - c;
  const pa = Math.abs(p - a);
  const pb = Math.abs(p - b);
  const pc = Math.abs(p - c);
  return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
}

function channel(v) {
  const s = v / 255;
  return s <= 0.03928 ? s / 12.92 : ((s + 0.055) / 1.055) ** 2.4;
}

function luminance([r, g, b]) {
  return 0.2126 * channel(r) + 0.7152 * channel(g) + 0.0722 * channel(b);
}

function ratio(fg, bg) {
  const [hi, lo] = [luminance(fg), luminance(bg)].sort((x, y) => y - x);
  return (hi + 0.05) / (lo + 0.05);
}

const hex = (s) => [1, 3, 5].map((i) => parseInt(s.slice(i, i + 2), 16));

const fills = {};
const fillOf = (file) => (fills[file] ??= pixelsOf(file));

// [label, palette, foreground token, background (token or fill file), minimum]
const pairs = [
  ['parchmentInk on parchment fill (body text)', 'day', 'parchmentInk', 'parchment-fill.png', 4.5],
  ['parchmentInk on parchment (flat)', 'day', 'parchmentInk', 'parchment', 4.5],
  ['stoneInk on stone fill (titles)', 'day', 'stoneInk', 'stone-fill.png', 4.5],
  ['stoneInk on stone (flat)', 'day', 'stoneInk', 'stone', 4.5],
  ['night stoneInk on night fill (titles)', 'night', 'stoneInk', 'stone-night-fill.png', 4.5],
  ['night stoneInk on night stone (flat)', 'night', 'stoneInk', 'stone', 4.5],
  ['ironInk on iron fill (marker text)', 'day', 'ironInk', 'iron-fill.png', 4.5],
  ['ironInk on iron (flat)', 'day', 'ironInk', 'iron', 4.5],
  ['brass on stone fill (graphics)', 'day', 'brass', 'stone-fill.png', 3],
  ['brass on stone (flat graphics)', 'day', 'brass', 'stone', 3],
  ['night brass on night fill (graphics)', 'night', 'brass', 'stone-night-fill.png', 3],
  ['night brass on night stone (flat)', 'night', 'brass', 'stone', 3],
  ['olive on parchment fill (graphics)', 'day', 'olive', 'parchment-fill.png', 3],
  ['olive on parchment (flat graphics)', 'day', 'olive', 'parchment', 3],
  ['iron on parchment (frames)', 'day', 'iron', 'parchment', 3],
];

const at = (scale, step) => tokens.scales[scale][step - 1];
const groundOf = (scheme, role) => {
  const [scale, step] = tokens.roles[scheme][role];
  return at(scale, step);
};
// The heraldic banners sit on the page grounds, day on light and night on dark.
for (const medium of ['bannerBook', 'bannerGame', 'bannerFilm']) {
  for (const role of ['ground', 'surface']) {
    pairs.push([`${medium} on light ${role} (graphics)`, 'day', medium, groundOf('light', role), 3]);
    pairs.push([`night ${medium} on dark ${role} (graphics)`, 'night', medium, groundOf('dark', role), 3]);
  }
}

const palettes = { day: castle, night };

let failed = false;
console.log('| Pair | Worst | Minimum |');
console.log('| ---- | ----: | ------: |');
for (const [label, which, fg, bg, min] of pairs) {
  const palette = palettes[which];
  const worst = bg.endsWith('.png')
    ? Math.min(...fillOf(bg).map((px) => ratio(hex(palette[fg]), px)))
    : ratio(hex(palette[fg]), hex(bg.startsWith('#') ? bg : palette[bg]));
  if (worst < min) failed = true;
  console.log(`| ${label} | ${worst.toFixed(2)}:1 | ${min.toFixed(1)}:1 |`);
}
process.exit(failed ? 1 : 0);
