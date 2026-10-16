// The swappable pieces of the Me tab, set once at app start and replaced by fakes in tests:
// Face ID, the vision engine's availability, the setting storage and how a finished export reaches the person.
import { Share } from 'react-native';
import { apiBaseUrl } from '@/lib/http';
import type { RecognitionState } from '@/features/scan/recognition/EngineSelector';
import { type KeyValueStore, MemoryKeyValueStore } from '@/features/scan/tray/trayStore';

export interface Biometrics {
  /** True when this phone can ask for Face ID or the device passcode. */
  isAvailable(): Promise<boolean>;
  /** Asks for Face ID, falling back to the device passcode. False when the person fails or cancels. */
  authenticate(reason: string): Promise<boolean>;
}

/** expo-local-authentication, loaded on first use so web and tests never need the native module. */
class DeviceBiometrics implements Biometrics {
  private module() {
    // eslint-disable-next-line @typescript-eslint/no-require-imports
    return require('expo-local-authentication') as typeof import('expo-local-authentication');
  }
  async isAvailable() {
    try {
      const auth = this.module();
      return (await auth.hasHardwareAsync()) && (await auth.getEnrolledLevelAsync()) !== auth.SecurityLevel.NONE;
    } catch {
      return false;
    }
  }
  async authenticate(reason: string) {
    try {
      const result = await this.module().authenticateAsync({ promptMessage: reason, disableDeviceFallback: false });
      return result.success;
    } catch {
      return false;
    }
  }
}

export interface RecognitionStatus {
  /** The engine the next snap will try first, and the Apple Intelligence state behind that choice. */
  state(): Promise<RecognitionState>;
}

/** The recognition selector's own answer, loaded on first use so the native module stays out of the web bundle and tests. */
const deviceVision: RecognitionStatus = {
  state: async () => {
    // eslint-disable-next-line @typescript-eslint/no-require-imports
    const { getRecognition } = require('@/features/scan/recognition/device') as typeof import('@/features/scan/recognition/device');
    return getRecognition().selector.state();
  },
};

export interface ExportSharer {
  /** Web: the browser downloads the file. iPhone: the share sheet opens. */
  deliver(downloadUrl: string): Promise<void>;
}

const absolute = (url: string) => (url.startsWith('/') ? `${apiBaseUrl()}${url}` : url);

const deviceSharer: ExportSharer = {
  deliver: async (downloadUrl) => {
    const url = absolute(downloadUrl);
    if (typeof document !== 'undefined') {
      const link = document.createElement('a');
      link.href = url;
      link.download = 'collectionmaxxing-export.zip';
      document.body.appendChild(link);
      link.click();
      link.remove();
      return;
    }
    await Share.share({ url });
  },
};

function openSettingsStorage(): KeyValueStore {
  try {
    // eslint-disable-next-line @typescript-eslint/no-require-imports
    const { createMMKV } = require('react-native-mmkv') as typeof import('react-native-mmkv');
    return createMMKV({ id: 'me-settings' });
  } catch {
    return new MemoryKeyValueStore();
  }
}

let biometrics: Biometrics = new DeviceBiometrics();
let vision: RecognitionStatus = deviceVision;
let sharer: ExportSharer = deviceSharer;
let storage: KeyValueStore | undefined;

export const getBiometrics = () => biometrics;
export const setBiometrics = (next: Biometrics) => {
  biometrics = next;
};
export const getVision = () => vision;
export const setVision = (next: RecognitionStatus) => {
  vision = next;
};
export const getSharer = () => sharer;
export const setSharer = (next: ExportSharer) => {
  sharer = next;
};
export const getSettingsStorage = (): KeyValueStore => (storage ??= openSettingsStorage());
export const setSettingsStorage = (next: KeyValueStore | undefined) => {
  storage = next;
};

/** How often the export screen asks whether the job has finished. Tests shorten it. */
export const exportPoll = { intervalMs: 1500 };
