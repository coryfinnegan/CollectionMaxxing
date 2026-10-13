import { SYMBOLOGIES, type DetectedCode, type Rect, type Symbology } from './types';

/** The part of VisionCamera's ScannedObject/ScannedCode that we read (kept structural so tests need no native code). */
export interface ScannedObjectLike {
  type: string;
  value?: string;
  boundingBox: Rect;
}

const supported = new Set<string>(SYMBOLOGIES);

/** Keeps only the codes we care about (EAN-13/8, UPC-E, QR, Code 128) that carry a value. */
export function mapScannedObjects(objects: readonly ScannedObjectLike[]): DetectedCode[] {
  const codes: DetectedCode[] = [];
  for (const o of objects) {
    if (!supported.has(o.type) || !o.value) continue;
    const { x, y, width, height } = o.boundingBox;
    codes.push({ value: o.value, symbology: o.type as Symbology, bounds: { x, y, width, height } });
  }
  return codes;
}
