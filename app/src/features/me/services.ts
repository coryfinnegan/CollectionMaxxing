// The swappable pieces of the Me tab, set once at app start and replaced by fakes in tests:
// Face ID, the vision engine's availability, the setting storage and how a finished export reaches the person.
import { Share } from 'react-native';
import { authHeaders } from '@/lib/auth/session';
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

export interface AppIconSwitcher {
  /** False where alternates don't exist (the web, an iPhone that declines them). */
  supportsAlternateIcons(): Promise<boolean>;
  /** The active alternate's plist name, or null for the default icon. */
  currentIconName(): Promise<string | null>;
  /** Applies the alternate, or null for the default icon. */
  setIconName(name: string | null): Promise<void>;
}

/** cm-app-icon, loaded on first use so web and tests never need the native module. */
class DeviceAppIconSwitcher implements AppIconSwitcher {
  private engine() {
    // eslint-disable-next-line @typescript-eslint/no-require-imports
    const { appIcon } = require('@/native/cm-app-icon') as typeof import('@/native/cm-app-icon');
    return appIcon;
  }
  supportsAlternateIcons() {
    return this.engine().supportsAlternateIcons();
  }
  currentIconName() {
    return this.engine().currentIconName();
  }
  setIconName(name: string | null) {
    return this.engine().setIconName(name);
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
  /** Web: the browser downloads the file. iPhone: the file is fetched with the sign-in, then the share sheet opens on it. */
  deliver(downloadUrl: string): Promise<void>;
}

const absolute = (url: string) => (url.startsWith('/') ? `${apiBaseUrl()}${url}` : url);

/** The iPhone's pieces for handing an export over. Tests swap them for fakes; nothing here runs on the web. */
export interface ExportDelivery {
  /**
   * Fetches `url` with `headers` into a temporary file and returns the file's `file://` address. Rejects, with nothing left on disk,
   * when the download fails or when what was saved is not the export (an ended sign-in or a server error saved as the .zip).
   */
  download(url: string, headers: Record<string, string>): Promise<string>;
  /** Opens the share sheet on the file; resolves when the sheet closes. */
  share(fileUrl: string): Promise<void>;
  /** Deletes the temporary file. Resolves when it is gone, or was never there. */
  remove(fileUrl: string): Promise<void>;
}

const EXPORT_FILE = 'collectionmaxxing-export.zip';

/** Every zip with files in it starts "PK" 3 4. */
const ZIP_SIGNATURE = [0x50, 0x4b, 0x03, 0x04];

export const isZipSignature = (head: Uint8Array): boolean => head.length >= ZIP_SIGNATURE.length && ZIP_SIGNATURE.every((byte, at) => head[at] === byte);

/**
 * A saved download is only the export if it starts like a zip. Otherwise (an error body saved under the .zip name, an empty file, a
 * file that cannot be read) the file is deleted and this throws, so the share sheet is never opened on it.
 */
export async function requireZipFile(
  fileUrl: string,
  readHead: (fileUrl: string) => Promise<Uint8Array>,
  remove: (fileUrl: string) => Promise<void>,
): Promise<void> {
  try {
    if (!isZipSignature(await readHead(fileUrl))) throw new Error("That download wasn't the export.");
  } catch (e) {
    await remove(fileUrl);
    throw e;
  }
}

/** expo-file-system's cache folder and React Native's share sheet, loaded on first use so the web and tests never need them. */
const deviceDelivery: ExportDelivery = {
  async download(url, headers) {
    // eslint-disable-next-line @typescript-eslint/no-require-imports
    const fs = require('expo-file-system') as typeof import('expo-file-system');
    // eslint-disable-next-line @typescript-eslint/no-require-imports
    const { discardPicture, trackPicture } = require('@/features/scan/pictureFiles') as typeof import('@/features/scan/pictureFiles');
    const dir = new fs.Directory(fs.Paths.cache, 'cm-export');
    if (!dir.exists) dir.create({ intermediates: true, idempotent: true });
    const destination = new fs.File(dir, EXPORT_FILE);
    // Registered before the download starts, so a file left by a killed app is deleted by the next sign-out or unpair.
    trackPicture(destination.uri);
    try {
      const file = await fs.File.downloadFileAsync(url, destination, { headers, idempotent: true });
      // Whether a refused answer (an ended sign-in, a server error) rejects here is not documented, so look at what was saved.
      await requireZipFile(
        file.uri,
        async () => {
          const handle = file.open();
          try {
            return handle.readBytes(ZIP_SIGNATURE.length); // four bytes: the zip holds photos and is never read whole
          } finally {
            handle.close();
          }
        },
        discardPicture,
      );
      return file.uri;
    } catch (e) {
      await discardPicture(destination.uri);
      throw e;
    }
  },
  async share(fileUrl) {
    await Share.share({ url: fileUrl });
  },
  async remove(fileUrl) {
    // eslint-disable-next-line @typescript-eslint/no-require-imports
    await (require('@/features/scan/pictureFiles') as typeof import('@/features/scan/pictureFiles')).discardPicture(fileUrl);
  },
};

/**
 * The download needs the same sign-in as every other call, and the phone's is a bearer token that a share target cannot send
 * (it would be handed a link it is refused at). So the app fetches the zip itself, with the token, and shares the *file*: the
 * share sheet gets a `file://` address that needs no sign-in. The file goes again once the sheet closes. The web keeps the
 * browser's own download, which carries the session cookie.
 */
export function createDeviceSharer(delivery: ExportDelivery, headersFor: (method: string) => Promise<Record<string, string>> = authHeaders): ExportSharer {
  return {
    deliver: async (downloadUrl) => {
      const url = absolute(downloadUrl);
      if (typeof document !== 'undefined') {
        const link = document.createElement('a');
        link.href = url;
        link.download = EXPORT_FILE;
        document.body.appendChild(link);
        link.click();
        link.remove();
        return;
      }
      const file = await delivery.download(url, await headersFor('GET'));
      try {
        await delivery.share(file);
      } finally {
        await delivery.remove(file).catch(() => {}); // a file that cannot be deleted now is found by the next clear
      }
    },
  };
}

const deviceSharer: ExportSharer = createDeviceSharer(deviceDelivery);

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
let appIcon: AppIconSwitcher = new DeviceAppIconSwitcher();
let storage: KeyValueStore | undefined;

export const getBiometrics = () => biometrics;
export const setBiometrics = (next: Biometrics) => {
  biometrics = next;
};
export const getAppIcon = () => appIcon;
export const setAppIcon = (next: AppIconSwitcher) => {
  appIcon = next;
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
