// The castle palette sign-off (#361): text reaches 4.5:1 and graphics 3:1, measured
// at the worst-case pixel under the text on the textured fills (brass and olive are
// accents/graphics only, never body text). The fills are decoded from the real PNGs,
// so regenerating the art re-verifies the ratios. scripts/castle-contrast.mjs prints
// the same table for the PR.
import { existsSync, readFileSync, statSync } from 'node:fs';
import { join } from 'node:path';
import { inflateSync } from 'node:zlib';
import { contrastRatio } from './contrast';
import { castle } from './tokens';

const dir = join(__dirname, '..', '..', 'assets', 'castle');

type Pixel = [number, number, number];

function paeth(a: number, b: number, c: number): number {
  const p = a + b - c;
  const pa = Math.abs(p - a);
  const pb = Math.abs(p - b);
  const pc = Math.abs(p - c);
  return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
}

// Minimal PNG decode: 8-bit RGB/RGBA, non-interlaced, which is what castle-textures.py writes.
function pixelsOf(file: string): Pixel[] {
  const raw = readFileSync(join(dir, file));
  let pos = 8;
  let width = 0;
  let height = 0;
  let bpp = 0;
  const idat: Buffer[] = [];
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
  const out: Pixel[] = [];
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

const toHex = ([r, g, b]: Pixel) => `#${[r, g, b].map((v) => v.toString(16).padStart(2, '0')).join('')}`;
const fills = new Map<string, Pixel[]>();
const fillOf = (file: string) => {
  const cached = fills.get(file);
  if (cached) return cached;
  const pixels = pixelsOf(file);
  fills.set(file, pixels);
  return pixels;
};

type CastleTone = keyof typeof castle;
type Pair = { fg: CastleTone; bg: CastleTone | string; min: number; use: string };
const isFill = (bg: CastleTone | string): bg is string => bg.endsWith('.png');
const worstOf = (fg: CastleTone, bg: CastleTone | string) =>
  isFill(bg) ? Math.min(...fillOf(bg).map((px) => contrastRatio(castle[fg], toHex(px)))) : contrastRatio(castle[fg], castle[bg]);

const text: Pair[] = [
  { fg: 'parchmentInk', bg: 'parchment-fill.png', min: 4.5, use: 'body text on parchment' },
  { fg: 'parchmentInk', bg: 'parchment', min: 4.5, use: 'body text on flat parchment' },
  { fg: 'stoneInk', bg: 'stone-fill.png', min: 4.5, use: 'titles on stone' },
  { fg: 'stoneInk', bg: 'stone', min: 4.5, use: 'titles on flat stone' },
  { fg: 'ironInk', bg: 'iron-fill.png', min: 4.5, use: 'marker text on iron' },
  { fg: 'ironInk', bg: 'iron', min: 4.5, use: 'marker text on flat iron' },
];
const graphics: Pair[] = [
  { fg: 'brass', bg: 'stone-fill.png', min: 3, use: 'brass rules on stone' },
  { fg: 'brass', bg: 'stone', min: 3, use: 'brass rules on flat stone' },
  { fg: 'olive', bg: 'parchment-fill.png', min: 3, use: 'olive marks on parchment' },
  { fg: 'olive', bg: 'parchment', min: 3, use: 'olive marks on flat parchment' },
  { fg: 'iron', bg: 'parchment', min: 3, use: 'iron frames on parchment' },
];

describe('castle palette contrast', () => {
  it.each(text)('text: $use ($fg) is at least 4.5:1 at its worst pixel', ({ fg, bg, min }) => {
    expect(worstOf(fg, bg)).toBeGreaterThanOrEqual(min);
  });
  it.each(graphics)('graphics: $use ($fg) is at least 3:1 at its worst pixel', ({ fg, bg, min }) => {
    expect(worstOf(fg, bg)).toBeGreaterThanOrEqual(min);
  });
});

describe('castle texture assets', () => {
  const assets = [
    'parchment-fill.png',
    'parchment-edge-h.png',
    'parchment-edge-v.png',
    'parchment-corner.png',
    'stone-fill.png',
    'stone-edge-h.png',
    'stone-edge-v.png',
    'stone-corner.png',
    'iron-fill.png',
    'crenel-strip.png',
    'ledge-strip.png',
  ];
  it.each(assets)('%s is a small tile, never a background image', (file) => {
    const path = join(dir, file);
    expect(existsSync(path)).toBe(true);
    expect(statSync(path).size).toBeLessThanOrEqual(8 * 1024);
    const raw = readFileSync(path);
    expect(raw.readUInt32BE(16)).toBeLessThanOrEqual(64);
    expect(raw.readUInt32BE(20)).toBeLessThanOrEqual(64);
  });
});
