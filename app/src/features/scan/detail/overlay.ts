// Where an item's box lands on the picture as it is shown (docs/06 M4). The picture is drawn "contain" inside its frame, so the
// box is placed in the picture's own rectangle, not the frame's. Pure, so every shape of picture can be tested.
import type { Rect } from '@/native/cm-vision';

export interface Size {
  width: number;
  height: number;
}

export interface Placed {
  left: number;
  top: number;
  width: number;
  height: number;
}

const round = (n: number) => Math.round(n * 100) / 100;

/** The rectangle the picture covers inside a frame, drawn contained and centred. */
export function fitImage(frame: Size, image: Size): Placed {
  const scale = Math.min(frame.width / image.width, frame.height / image.height);
  const width = image.width * scale;
  const height = image.height * scale;
  return { left: round((frame.width - width) / 2), top: round((frame.height - height) / 2), width: round(width), height: round(height) };
}

/** The box (normalised 0..1, origin top-left) in frame pixels, or nothing when there is no box to draw. */
export function boxOverlay(box: Rect | undefined, frame: Size, image: Size): Placed | undefined {
  if (!box || frame.width <= 0 || frame.height <= 0 || image.width <= 0 || image.height <= 0) return undefined;
  const x0 = Math.max(0, box.x);
  const y0 = Math.max(0, box.y);
  const x1 = Math.min(1, box.x + box.width);
  const y1 = Math.min(1, box.y + box.height);
  if (x1 <= x0 || y1 <= y0) return undefined;
  const shown = fitImage(frame, image);
  return {
    left: round(shown.left + x0 * shown.width),
    top: round(shown.top + y0 * shown.height),
    width: round((x1 - x0) * shown.width),
    height: round((y1 - y0) * shown.height),
  };
}
