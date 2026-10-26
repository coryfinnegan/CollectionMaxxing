// The shipped icon family (#364): the dragon master as the app icon and favicon, the Game Boy
// alternate for iOS, and the Android adaptive sets for both. Made by scripts/make-app-icons.py.
import { existsSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { inflateSync } from 'node:zlib';

const assets = join(__dirname, '..');

/** Width, height and color type (2 = RGB opaque, 6 = RGBA) from the PNG header. */
function pngInfo(rel: string) {
  const file = join(assets, rel);
  if (!existsSync(file)) throw new Error(`missing shipped asset: ${rel}`);
  const bytes = readFileSync(file);
  expect([...bytes.subarray(0, 8)]).toEqual([137, 80, 78, 71, 13, 10, 26, 10]);
  return { width: bytes.readUInt32BE(16), height: bytes.readUInt32BE(20), colorType: bytes[25] };
}

/** Every distinct RGB triple in an RGB PNG, by decoding the pixel data. */
function rgbColors(rel: string): Set<string> {
  const bytes = readFileSync(join(assets, rel));
  const { width, height, colorType } = pngInfo(rel);
  if (colorType !== 2) throw new Error(`${rel} is not an RGB PNG`);
  const idat: Buffer[] = [];
  let offset = 8;
  while (offset < bytes.length) {
    const length = bytes.readUInt32BE(offset);
    const type = bytes.subarray(offset + 4, offset + 8).toString('ascii');
    if (type === 'IDAT') idat.push(bytes.subarray(offset + 8, offset + 8 + length));
    if (type === 'IEND') break;
    offset += 12 + length;
  }
  const raw = inflateSync(Buffer.concat(idat));
  const stride = width * 3;
  const colors = new Set<string>();
  let previous = Buffer.alloc(stride);
  for (let y = 0; y < height; y++) {
    const filter = raw[y * (stride + 1)];
    const line = Buffer.from(raw.subarray(y * (stride + 1) + 1, (y + 1) * (stride + 1)));
    for (let x = 0; x < stride; x++) {
      const a = x >= 3 ? line[x - 3] : 0;
      const b = previous[x];
      const c = x >= 3 ? previous[x - 3] : 0;
      const predict = filter === 1 ? a : filter === 2 ? b : filter === 3 ? (a + b) >> 1 : filter === 4 ? paeth(a, b, c) : 0;
      line[x] = (line[x] + predict) & 0xff;
    }
    for (let x = 0; x < width; x++) colors.add(line.subarray(x * 3, x * 3 + 3).toString('hex'));
    previous = line;
  }
  return colors;
}

function paeth(a: number, b: number, c: number): number {
  const p = a + b - c;
  const pa = Math.abs(p - a);
  const pb = Math.abs(p - b);
  const pc = Math.abs(p - c);
  return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
}

describe('app icons', () => {
  it('shows the dragon master as the opaque 1024px app icon', () => {
    // Opaque: Apple rejects a primary icon with transparency.
    expect(pngInfo('images/icon.png')).toEqual({ width: 1024, height: 1024, colorType: 2 });
  });

  it('shows the dragon as the 48px web favicon', () => {
    expect(pngInfo('images/favicon.png')).toEqual({ width: 48, height: 48, colorType: 2 });
  });

  it('ships the Game Boy alternate as a 1024px master plus iOS @2x/@3x', () => {
    expect(pngInfo('icons/icon-gameboy.png')).toEqual({ width: 1024, height: 1024, colorType: 2 });
    expect(pngInfo('icons/icon-gameboy@2x.png')).toEqual({ width: 120, height: 120, colorType: 2 });
    expect(pngInfo('icons/icon-gameboy@3x.png')).toEqual({ width: 180, height: 180, colorType: 2 });
  });

  it('paints the Game Boy master in only the classic four greens', () => {
    // The @2x/@3x are smooth downscales, so only the master is exactly four colors.
    expect(rgbColors('icons/icon-gameboy.png')).toEqual(new Set(['0f380f', '306230', '8bac0f', '9bbc0f']));
  });

  it('ships Android adaptive foregrounds, backgrounds and monochromes for both variants', () => {
    expect(pngInfo('images/android-icon-foreground.png')).toEqual({ width: 512, height: 512, colorType: 6 });
    expect(pngInfo('images/android-icon-background.png')).toEqual({ width: 512, height: 512, colorType: 2 });
    expect(pngInfo('images/android-icon-monochrome.png')).toEqual({ width: 432, height: 432, colorType: 6 });
    expect(pngInfo('icons/android-gameboy-foreground.png')).toEqual({ width: 512, height: 512, colorType: 6 });
    expect(pngInfo('icons/android-gameboy-background.png')).toEqual({ width: 512, height: 512, colorType: 2 });
    expect(pngInfo('icons/android-gameboy-monochrome.png')).toEqual({ width: 432, height: 432, colorType: 6 });
  });
});
