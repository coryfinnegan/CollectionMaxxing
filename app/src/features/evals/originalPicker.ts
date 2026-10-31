// The photo library, asked for the ORIGINALS. Eval photos have to reach the PC exactly as the camera made them: a share sheet or an email
// shrinks them to 2048 px, but the scanner sees 2576 px and more, so a shrunk copy would measure the wrong thing. So the picker is told to
// hand over the file itself: no editing or crop, no re-compression, no transcoding of a HEIC to a JPEG, and a photo that lives in iCloud is
// downloaded in full first. Nothing here, or after it, runs the picture through expo-image-manipulator.
// Loaded when the person taps Add photos, so the Evals screen opens without it.

export interface PickedOriginal {
  /** The picker's copy of the original (same bytes, in the cache folder; a blob address on the web). */
  uri: string;
  width: number;
  height: number;
  fileName: string | null;
  mimeType: string | null;
  fileSize: number | null;
}

export type OriginalPickResult = { status: 'picked'; photos: PickedOriginal[] } | { status: 'cancelled' } | { status: 'denied' };

/** The system picker, asked for the originals. The real one is expo-image-picker; tests script this. */
export interface OriginalPicker {
  pick(): Promise<OriginalPickResult>;
}

/** Reads a picked original as the bytes to send. The phone reads the file address; tests swap it. */
export type OriginalReader = (uri: string) => Promise<Blob>;

export const readOriginal: OriginalReader = async (uri) => (await fetch(uri)).blob();

type ImagePickerModule = typeof import('expo-image-picker');

/**
 * The options that keep the original:
 * - `quality: 1` and no `allowsEditing`: the picker does not re-compress or crop the file.
 * - `preferredAssetRepresentationMode: Current`: the library's own representation (a HEIC stays a HEIC), not a JPEG made for compatibility.
 * - `shouldDownloadFromNetwork: true`: a photo kept in iCloud is fetched in full, not handed over as the small on-device preview.
 * - no `exif` and no `base64`: nothing extra is read or copied into memory; the file is read once, when it is sent.
 */
export const originalPickOptions = (picker: Pick<ImagePickerModule, 'UIImagePickerPreferredAssetRepresentationMode'>) => ({
  mediaTypes: ['images' as const],
  allowsMultipleSelection: true,
  orderedSelection: true,
  allowsEditing: false,
  quality: 1,
  exif: false,
  base64: false,
  preferredAssetRepresentationMode: picker.UIImagePickerPreferredAssetRepresentationMode.Current,
  shouldDownloadFromNetwork: true,
});

export const deviceOriginalPicker: OriginalPicker = {
  async pick() {
    // eslint-disable-next-line @typescript-eslint/no-require-imports
    const ImagePicker = require('expo-image-picker') as ImagePickerModule;
    const permission = await ImagePicker.requestMediaLibraryPermissionsAsync();
    if (!permission.granted && permission.accessPrivileges !== 'limited') return { status: 'denied' };
    const result = await ImagePicker.launchImageLibraryAsync(originalPickOptions(ImagePicker));
    if (result.canceled) return { status: 'cancelled' };
    return {
      status: 'picked',
      photos: result.assets.map((a) => ({
        uri: a.uri,
        width: a.width,
        height: a.height,
        fileName: a.fileName ?? null,
        mimeType: a.mimeType ?? null,
        fileSize: a.fileSize ?? null,
      })),
    };
  },
};
