// A snap's picture: the camera's full-resolution still, scaled to a long edge of at most 2576 px and saved as a JPEG
// on the phone. It is read on the phone; nothing here uploads it (docs/02 "Privacy").
export const MAX_LONG_EDGE = 2576;
export const JPEG_QUALITY = 0.85;

export interface CapturedPhoto {
  uri: string;
  width: number;
  height: number;
  /** EXIF orientation, 1..8. */
  orientation: number;
}

export interface SnapPhoto {
  uri: string;
  width: number;
  height: number;
}

/** The size to scale to, or null when the picture is small enough already. */
export function fitLongEdge(width: number, height: number, max = MAX_LONG_EDGE): { width: number; height: number } | null {
  const long = Math.max(width, height);
  if (long <= max || width <= 0 || height <= 0) return null;
  const scale = max / long;
  return { width: Math.max(1, Math.round(width * scale)), height: Math.max(1, Math.round(height * scale)) };
}

export type Resizer = (photo: CapturedPhoto, target: { width: number; height: number } | null) => Promise<SnapPhoto>;

/** The real resizer: expo-image-manipulator, loaded when the first snap needs it. */
export const manipulatorResizer: Resizer = async (photo, target) => {
  const { ImageManipulator, SaveFormat } = await import('expo-image-manipulator');
  let fit = target;
  if (photo.width <= 0 || photo.height <= 0) {
    // The camera did not say how big the still is; read it, then decide.
    const probe = await ImageManipulator.manipulate(photo.uri).renderAsync();
    fit = fitLongEdge(probe.width, probe.height);
  }
  const context = ImageManipulator.manipulate(photo.uri);
  if (fit) context.resize({ width: fit.width, height: fit.height });
  const image = await context.renderAsync();
  const saved = await image.saveAsync({ format: SaveFormat.JPEG, compress: JPEG_QUALITY });
  return { uri: saved.uri, width: saved.width, height: saved.height };
};

export const prepareSnapPhoto = (photo: CapturedPhoto, resize: Resizer = manipulatorResizer): Promise<SnapPhoto> =>
  resize(photo, fitLongEdge(photo.width, photo.height));
