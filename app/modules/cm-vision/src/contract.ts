import type { DetectedCode, FeaturePrint, Rect, TextLine } from './types';

// The runtime key lists below are compared with contract.schema.json (and the Swift builders)
// by Jest. The `true` assignments fail to type-check when a field is added to an interface
// without being listed here.
export const RECT_KEYS = ['x', 'y', 'width', 'height'] as const;
export const TEXT_LINE_KEYS = ['text', 'confidence', 'box'] as const;
export const FEATURE_PRINT_KEYS = ['revision', 'data'] as const;
export const DETECTED_CODE_KEYS = ['value', 'symbology', 'bounds'] as const;

type Unlisted<T, K extends PropertyKey> = Exclude<keyof T, K>;
export const keyListsAreComplete: [
  Unlisted<Rect, (typeof RECT_KEYS)[number]> extends never ? true : false,
  Unlisted<TextLine, (typeof TEXT_LINE_KEYS)[number]> extends never ? true : false,
  Unlisted<FeaturePrint, (typeof FEATURE_PRINT_KEYS)[number]> extends never ? true : false,
  Unlisted<DetectedCode, (typeof DETECTED_CODE_KEYS)[number]> extends never ? true : false,
] = [true, true, true, true];

/** Keys by schema definition name. */
export const CONTRACT_KEYS = {
  Rect: RECT_KEYS,
  TextLine: TEXT_LINE_KEYS,
  FeaturePrint: FEATURE_PRINT_KEYS,
  DetectedCode: DETECTED_CODE_KEYS,
} as const;
