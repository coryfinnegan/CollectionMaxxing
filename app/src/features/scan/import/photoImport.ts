// Import from Photos (docs/02): pictures already taken go through exactly the same on-phone pipeline as a live snap, one after
// another. A picture with a readable barcode takes the barcode path instead and is not also read as a picture. Nothing is
// uploaded: the picked copy and the scaled copy are registered as picture files and deleted as soon as they have been used.
import type { DetectedCode } from '@/native/cm-vision';
import { prepareSnapPhoto, type Resizer } from '../autosnap/snapPhoto';
import type { SnapEvent } from '../autosnap/useAutoSnap';
import { discardPicture, trackPicture } from '../pictureFiles';
import type { Reader } from '../pipeline/SnapPipeline';
import type { ReadItem, SnapReading, Still } from '../recognition/types';
import type { SeenCode } from '../scanner/ScannerSource';

/** The most pictures one pick can bring in. */
export const MAX_PER_PICK = 20;

export interface PickedImage {
  uri: string;
  width: number;
  height: number;
}

export type PickResult = { status: 'picked'; images: PickedImage[] } | { status: 'cancelled' } | { status: 'denied' };

/** The system picker. The real one is expo-image-picker; tests script this. */
export interface PhotoPicker {
  pick(max: number): Promise<PickResult>;
}

export interface BarcodeDetector {
  detectBarcodes(imageUri: string): Promise<DetectedCode[]>;
}

export interface ImportDeps {
  picker: PhotoPicker;
  vision: () => BarcodeDetector | Promise<BarcodeDetector>;
  /** The pipeline's entry for one picture, with the reader to use. */
  snap: (event: SnapEvent, options: { recognition: Reader }) => Promise<void>;
  recognition: Reader;
  /** Codes found in a picture, handed to the barcode path as if the camera held them steady. */
  onCodes: (codes: SeenCode[]) => void;
  resize?: Resizer;
}

export interface ImportState {
  running: boolean;
  /** The picture being read, 1-based, and how many there are. */
  index: number;
  total: number;
  /** What to tell the person once it stops; empty when there is nothing to say. */
  message: string;
}

export const IDLE: ImportState = { running: false, index: 0, total: 0, message: '' };

export const DENIED_COPY = 'Photos access is off. Turn it on in Settings to import pictures.';
export const PICKER_FAILED_COPY = "Couldn't open your photos. Try again.";

const pictures = (n: number) => `${n} ${n === 1 ? 'picture' : 'pictures'}`;

export function summaryOf(counts: { empty: number; failed: number; skipped: number }): string {
  const parts: string[] = [];
  if (counts.failed > 0) parts.push(`Couldn't open ${pictures(counts.failed)}.`);
  if (counts.empty > 0) parts.push(`Nothing to read in ${pictures(counts.empty)}.`);
  if (counts.skipped > 0) parts.push(`Stopped with ${pictures(counts.skipped)} left.`);
  return parts.join(' ');
}

/** Wraps the reader to learn what a picture gave: how many items, or that it could not be read. */
function watched(inner: Reader) {
  const seen = { items: 0, failed: false };
  const reader: Reader = {
    async read(still: Still, onItem: (item: ReadItem) => void): Promise<SnapReading> {
      try {
        const reading = await inner.read(still, onItem);
        seen.items = reading.items.length;
        return reading;
      } catch (error) {
        seen.failed = true;
        throw error;
      }
    },
  };
  return { reader, seen };
}

export class PhotoImport {
  private state: ImportState = IDLE;
  private readonly listeners = new Set<() => void>();
  private cancelled = false;

  constructor(private readonly deps: ImportDeps) {}

  getState = () => this.state;

  subscribe = (listener: () => void) => {
    this.listeners.add(listener);
    return () => void this.listeners.delete(listener);
  };

  private set(next: Partial<ImportState>) {
    this.state = { ...this.state, ...next };
    for (const listener of [...this.listeners]) listener();
  }

  /** Stops after the picture being read; the rest are not read. */
  cancel() {
    this.cancelled = true;
  }

  /** Opens the picker and brings in what is chosen. Does nothing while an import is running. */
  async start(): Promise<void> {
    if (this.state.running) return;
    this.cancelled = false;
    this.set({ running: true, index: 0, total: 0, message: '' });
    let picked: PickResult;
    try {
      picked = await this.deps.picker.pick(MAX_PER_PICK);
    } catch {
      this.set({ ...IDLE, message: PICKER_FAILED_COPY });
      return;
    }
    if (picked.status === 'denied') {
      this.set({ ...IDLE, message: DENIED_COPY });
      return;
    }
    if (picked.status === 'cancelled' || picked.images.length === 0) {
      this.set(IDLE);
      return;
    }
    const images = picked.images.slice(0, MAX_PER_PICK);
    for (const image of images) trackPicture(image.uri); // each picked copy is deleted once read, whatever happens
    const counts = { empty: 0, failed: 0, skipped: 0 };
    this.set({ total: images.length, index: 1 });
    for (let i = 0; i < images.length; i += 1) {
      const image = images[i] as PickedImage;
      if (this.cancelled) {
        counts.skipped = images.length - i;
        await Promise.all(images.slice(i).map((rest) => discardPicture(rest.uri)));
        break;
      }
      this.set({ index: i + 1 });
      try {
        const outcome = await this.one(image);
        if (outcome === 'empty') counts.empty += 1;
        else if (outcome === 'failed') counts.failed += 1;
      } catch {
        counts.failed += 1; // one bad file never stops the rest
      } finally {
        await discardPicture(image.uri);
      }
    }
    this.set({ ...IDLE, message: summaryOf(counts) });
  }

  private async one(image: PickedImage): Promise<'read' | 'barcode' | 'empty' | 'failed'> {
    const codes = await this.barcodes(image.uri);
    if (codes.length > 0) {
      this.deps.onCodes(codes);
      return 'barcode'; // a readable barcode wins: the barcode path has it, no picture is sent
    }
    const photo = await prepareSnapPhoto({ ...image, orientation: 1 }, this.deps.resize); // deletes its input once scaled
    const { reader, seen } = watched(this.deps.recognition);
    await this.deps.snap({ photo, trigger: 'import' }, { recognition: reader });
    if (seen.failed) return 'failed';
    return seen.items === 0 ? 'empty' : 'read';
  }

  private async barcodes(uri: string): Promise<SeenCode[]> {
    try {
      const found = await (await this.deps.vision()).detectBarcodes(uri);
      return found.map((c) => ({ value: c.value, symbology: c.symbology }));
    } catch {
      return []; // a picture that cannot be checked for barcodes is read as a picture
    }
  }
}
