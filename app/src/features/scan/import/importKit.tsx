// Test setup for Import from Photos: a scripted picker and barcode reader on the pipeline kit's fake file system.
import { fireEvent, screen } from '@testing-library/react-native';
import { MOCK_LIBRARY_ID } from '@/mocks/data';
import { renderWithProviders } from '@/test/render';
import type { DetectedCode } from '@/native/cm-vision';
import type { Resizer } from '../autosnap/snapPhoto';
import { ScannerScreen } from '../scanner/ScannerScreen';
import { FakeScannerSource } from '../scanner/ScannerSource';
import type { makeKit } from '../pipeline/testKit';
import type { PhotoPicker, PickResult } from './photoImport';

export const tuning = { retryMs: 20, pollMs: 20, resendMs: 40, flashMs: 50 };

/** Hands over what the test scripted, made as files on the fake phone, the way the system picker's cache copies are. */
export class FakePicker implements PhotoPicker {
  picks: PickResult[] = [];
  asked: number[] = [];
  private made = 0;
  constructor(private readonly kit: ReturnType<typeof makeKit>) {}

  /** The next pick returns this many fresh images. */
  images(count: number): string[] {
    const uris = Array.from({ length: count }, () => this.kit.fs.create(`file:///var/Caches/ImagePicker/pick-${++this.made}.jpg`));
    this.picks.push({ status: 'picked', images: uris.map((uri) => ({ uri, width: 4032, height: 3024 })) });
    return uris;
  }
  async pick(max: number): Promise<PickResult> {
    this.asked.push(max);
    return this.picks.shift() ?? { status: 'cancelled' };
  }
}

/** Barcodes by picture; throws for the pictures listed in `unreadable`. */
export class FakeBarcodes {
  codes = new Map<string, DetectedCode[]>();
  checked: string[] = [];
  async detectBarcodes(uri: string): Promise<DetectedCode[]> {
    this.checked.push(uri);
    return this.codes.get(uri) ?? [];
  }
}

export const code = (value: string): DetectedCode => ({ value, symbology: 'ean-13', bounds: { x: 0, y: 0, width: 1, height: 0.2 } });

export function importKit(kit: ReturnType<typeof makeKit>) {
  const picker = new FakePicker(kit);
  const barcodes = new FakeBarcodes();
  let scaled = 0;
  /** Scales into a new file, as expo-image-manipulator does. */
  const resize: Resizer = async (photo, target) => ({
    uri: kit.fs.create(`file:///var/Caches/ImageManipulator/import-${++scaled}.jpg`),
    width: target?.width ?? photo.width,
    height: target?.height ?? photo.height,
  });
  const open = async (source = new FakeScannerSource()) => {
    await renderWithProviders(
      <ScannerScreen
        libraryId={MOCK_LIBRARY_ID}
        source={source}
        pipeline={kit.deps}
        tuning={tuning}
        photoImport={{ picker, vision: () => barcodes, resize }}
      />,
    );
    return source;
  };
  const pressPhotos = async () => {
    await fireEvent.press(screen.getByLabelText('Photos'));
  };
  return { picker, barcodes, open, pressPhotos };
}
