// The real picker: the system photo picker through expo-image-picker. Images only, several at once. Loaded when the person taps
// Photos, so the scanner opens without it. The picked copies live in the cache folder and are registered by the importer.
import type { BarcodeDetector, PhotoPicker } from './photoImport';

export const devicePicker: PhotoPicker = {
  async pick(max) {
    // eslint-disable-next-line @typescript-eslint/no-require-imports
    const ImagePicker = require('expo-image-picker') as typeof import('expo-image-picker');
    const permission = await ImagePicker.requestMediaLibraryPermissionsAsync();
    if (!permission.granted && permission.accessPrivileges !== 'limited') return { status: 'denied' };
    const result = await ImagePicker.launchImageLibraryAsync({
      mediaTypes: ['images'],
      allowsMultipleSelection: true,
      selectionLimit: max,
      orderedSelection: true,
      quality: 1,
    });
    if (result.canceled) return { status: 'cancelled' };
    return { status: 'picked', images: result.assets.map((a) => ({ uri: a.uri, width: a.width, height: a.height })) };
  },
};

export const deviceBarcodes = async (): Promise<BarcodeDetector> => (await import('@/native/cm-vision')).visionEngine;
