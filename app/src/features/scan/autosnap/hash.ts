// Perceptual hashes as hex strings, and how far apart two of them are.

/** The share of differing bits, 0 (identical) to 1 (opposite). Hashes of different length are as far apart as can be. */
export function hashDistance(a: string, b: string): number {
  if (a.length === 0 || a.length !== b.length) return 1;
  let differing = 0;
  for (let i = 0; i < a.length; i++) {
    const x = parseInt(a[i]!, 16) ^ parseInt(b[i]!, 16);
    if (Number.isNaN(x)) return 1;
    differing += (x & 1) + ((x >> 1) & 1) + ((x >> 2) & 1) + ((x >> 3) & 1);
  }
  return differing / (a.length * 4);
}

/** 64 bits from the first 64 floats of a feature print (base64 of float32s): each bit says "above the average". */
export function hashFromFeaturePrint(base64: string): string | null {
  try {
    const bytes = Uint8Array.from(globalThis.atob(base64), (c) => c.charCodeAt(0));
    const count = Math.min(64, Math.floor(bytes.byteLength / 4));
    if (count < 64) return null;
    const view = new DataView(bytes.buffer);
    const values = Array.from({ length: count }, (_, i) => view.getFloat32(i * 4, true));
    if (values.some((v) => !Number.isFinite(v))) return null;
    const mean = values.reduce((sum, v) => sum + v, 0) / count;
    let hex = '';
    for (let nibble = 0; nibble < 16; nibble++) {
      let n = 0;
      for (let bit = 0; bit < 4; bit++) n = (n << 1) | ((values[nibble * 4 + bit] ?? 0) > mean ? 1 : 0);
      hex += n.toString(16);
    }
    return hex;
  } catch {
    return null;
  }
}
