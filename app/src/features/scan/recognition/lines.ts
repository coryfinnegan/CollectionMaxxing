// Reading text from a still: three orientations, noise dropped, the same line seen twice kept once.
import type { TextLine, VisionEngine } from '@/native/cm-vision';
import { overlapShare } from './geometry';
import type { LocalTuning } from './defaults';

export const normalize = (text: string): string =>
  text
    .normalize('NFKD')
    .replace(/[̀-ͯ]/g, '')
    .toLowerCase()
    .replace(/[^\p{L}\p{N}]+/gu, ' ')
    .trim();

export const wordsOf = (text: string): string[] => normalize(text).split(' ').filter(Boolean);

export function cleanLines(lines: TextLine[], tuning: LocalTuning): TextLine[] {
  const kept: TextLine[] = [];
  const byConfidence = [...lines].filter((l) => l.confidence >= tuning.minTextConfidence && normalize(l.text) !== '');
  byConfidence.sort((a, b) => b.confidence - a.confidence);
  for (const candidate of byConfidence) {
    const same = kept.some(
      (k) => normalize(k.text) === normalize(candidate.text) && overlapShare(k.box, candidate.box) >= tuning.duplicateLineOverlap,
    );
    if (!same) kept.push(candidate);
  }
  return kept.sort((a, b) => a.box.x - b.box.x || a.box.y - b.box.y);
}

/** Upright, then turned 90 and 270 degrees, so lines on spines are read too. Never throws: no text is a reading too. */
export async function readLines(vision: VisionEngine, uri: string, tuning: LocalTuning): Promise<TextLine[]> {
  try {
    return cleanLines(await vision.recognizeText(uri, { orientations: [0, 90, 270] }), tuning);
  } catch {
    return [];
  }
}
