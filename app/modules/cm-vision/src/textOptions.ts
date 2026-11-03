import type { RecognizeTextOptions, Rect, TextLevel, TextOrientation } from './types';

/** What crosses to Swift beyond the orientations. Only what the caller named, so Swift's own defaults stay in force for the rest. */
export interface NativeTextOptions {
  level?: TextLevel;
  languageCorrection?: boolean;
  regions?: Rect[];
}

/** A text read's options with every default written out, checked: regions are read upright, so they cannot be asked for in a turned pass. */
export interface TextRequest {
  orientations: TextOrientation[];
  level: TextLevel;
  languageCorrection: boolean;
  regions: Rect[];
}

export function textRequest(options?: RecognizeTextOptions): TextRequest {
  const orientations = options?.orientations ?? [0];
  const regions = options?.regions ?? [];
  if (regions.length > 0 && orientations.some((orientation) => orientation !== 0)) {
    throw new Error('Regions are read upright: ask for orientation 0 only.');
  }
  return { orientations, level: options?.level ?? 'accurate', languageCorrection: options?.languageCorrection ?? false, regions };
}

/** The options Swift is given: what the caller named, or null when it named none (the scanner's own request). An empty list of regions is the whole picture. */
export function nativeTextOptions(options?: RecognizeTextOptions): NativeTextOptions | null {
  const named: NativeTextOptions = {
    ...(options?.level === undefined ? {} : { level: options.level }),
    ...(options?.languageCorrection === undefined ? {} : { languageCorrection: options.languageCorrection }),
    ...(options?.regions && options.regions.length > 0 ? { regions: options.regions } : {}),
  };
  return Object.keys(named).length > 0 ? named : null;
}
