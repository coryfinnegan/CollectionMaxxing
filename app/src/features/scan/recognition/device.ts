// The real recognition pieces on the phone: the vision module, the config cache in MMKV, the config fetch and the
// cropper for wide shots. Tests build their own pieces instead; nothing here runs on the web build.
import { AppState } from 'react-native';
import { getRecognitionConfig } from '@/api/generated/recognition/recognition';
import { MemoryKeyValueStore, type KeyValueStore } from '@/features/scan/tray/trayStore';
import { ApiError } from '@/lib/http';
import { RecognitionConfigStore, type ConfigFetcher } from './config';
import { HTTP_NOT_MODIFIED } from './defaults';
import { EngineSelector } from './EngineSelector';
import type { Still, StillCropper } from './types';

export interface Recognition {
  store: RecognitionConfigStore;
  selector: EngineSelector;
}

const fetchConfig: ConfigFetcher = async (etag) => {
  try {
    const response = await getRecognitionConfig(etag ? { headers: { 'If-None-Match': etag } } : undefined);
    return { status: 'ok', etag: response.headers.get('ETag') ?? undefined, body: response.data };
  } catch (error) {
    if (error instanceof ApiError && error.status === HTTP_NOT_MODIFIED) return { status: 'notModified' };
    throw error;
  }
};

/** expo-image-manipulator, loaded on first use. The crop stays on the phone and is only handed to the vision module. */
const deviceCropper: StillCropper = {
  async crop(still: Still, region) {
    // eslint-disable-next-line @typescript-eslint/no-require-imports
    const { ImageManipulator, SaveFormat } = require('expo-image-manipulator') as typeof import('expo-image-manipulator');
    const rendered = await ImageManipulator.manipulate(still.uri)
      .crop({
        originX: Math.round(region.x * still.width),
        originY: Math.round(region.y * still.height),
        width: Math.round(region.width * still.width),
        height: Math.round(region.height * still.height),
      })
      .renderAsync();
    const saved = await rendered.saveAsync({ format: SaveFormat.JPEG });
    return { uri: saved.uri, width: saved.width, height: saved.height };
  },
};

function openStorage(): KeyValueStore {
  try {
    // eslint-disable-next-line @typescript-eslint/no-require-imports
    const { createMMKV } = require('react-native-mmkv') as typeof import('react-native-mmkv');
    return createMMKV({ id: 'recognition-config' });
  } catch {
    return new MemoryKeyValueStore();
  }
}

let current: Recognition | undefined;

/** The recognition engines and their config, built on first use. */
export function getRecognition(): Recognition {
  if (!current) {
    // eslint-disable-next-line @typescript-eslint/no-require-imports
    const { visionEngine } = require('@/native/cm-vision') as typeof import('@/native/cm-vision');
    const store = new RecognitionConfigStore({ storage: openStorage(), fetcher: fetchConfig });
    current = { store, selector: new EngineSelector({ vision: visionEngine, settings: () => store.current(), cropper: deviceCropper }) };
  }
  return current;
}

export const setRecognition = (next: Recognition | undefined) => {
  current = next;
};

/**
 * Refreshes the config now and each time the app comes to the front. Call it when the scanner opens; it returns the
 * function that stops watching. Never blocks and never throws.
 */
export function watchRecognitionConfig(): () => void {
  void getRecognition().store.refresh();
  const subscription = AppState.addEventListener('change', (state) => {
    if (state === 'active') void getRecognition().store.refresh();
  });
  return () => subscription.remove();
}
