import type { DetectedCode, DeviceStatus, FeaturePrint, ModelCapabilities, ModelItem, ReadItemsResult, Rect, TextLine } from './types';

// The runtime key lists below are compared with contract.schema.json (and the Swift builders)
// by Jest. The `true` assignments fail to type-check when a field is added to an interface
// without being listed here.
export const RECT_KEYS = ['x', 'y', 'width', 'height'] as const;
export const TEXT_LINE_KEYS = ['text', 'confidence', 'box'] as const;
export const FEATURE_PRINT_KEYS = ['revision', 'data'] as const;
export const DETECTED_CODE_KEYS = ['value', 'symbology', 'bounds'] as const;
export const MODEL_ITEM_KEYS = [
  'visibleText', 'title', 'creator', 'kind', 'platform', 'editionMarks', 'completeness', 'order', 'confidence',
] as const;
export const MODEL_CAPABILITIES_KEYS = ['available', 'vision', 'guidedGeneration', 'contextSize', 'reason'] as const;
export const READ_ITEMS_RESULT_KEYS = ['items', 'unreadableCount', 'outcome', 'elapsedMs'] as const;
export const DEVICE_STATUS_KEYS = ['thermalState', 'osVersion'] as const;

/** Keys that may be absent (the schema's `required` list leaves them out). */
export const OPTIONAL_KEYS: Record<string, readonly string[]> = {
  ModelItem: ['creator', 'platform', 'editionMarks', 'completeness'],
  ModelCapabilities: ['reason'],
};

type Unlisted<T, K extends PropertyKey> = Exclude<keyof T, K>;
export const keyListsAreComplete: [
  Unlisted<Rect, (typeof RECT_KEYS)[number]> extends never ? true : false,
  Unlisted<TextLine, (typeof TEXT_LINE_KEYS)[number]> extends never ? true : false,
  Unlisted<FeaturePrint, (typeof FEATURE_PRINT_KEYS)[number]> extends never ? true : false,
  Unlisted<DetectedCode, (typeof DETECTED_CODE_KEYS)[number]> extends never ? true : false,
  Unlisted<ModelItem, (typeof MODEL_ITEM_KEYS)[number]> extends never ? true : false,
  Unlisted<ModelCapabilities, (typeof MODEL_CAPABILITIES_KEYS)[number]> extends never ? true : false,
  Unlisted<ReadItemsResult, (typeof READ_ITEMS_RESULT_KEYS)[number]> extends never ? true : false,
  Unlisted<DeviceStatus, (typeof DEVICE_STATUS_KEYS)[number]> extends never ? true : false,
] = [true, true, true, true, true, true, true, true];

/** Keys by schema definition name. */
export const CONTRACT_KEYS = {
  Rect: RECT_KEYS,
  TextLine: TEXT_LINE_KEYS,
  FeaturePrint: FEATURE_PRINT_KEYS,
  DetectedCode: DETECTED_CODE_KEYS,
  ModelItem: MODEL_ITEM_KEYS,
  ModelCapabilities: MODEL_CAPABILITIES_KEYS,
  ReadItemsResult: READ_ITEMS_RESULT_KEYS,
  DeviceStatus: DEVICE_STATUS_KEYS,
} as const;
