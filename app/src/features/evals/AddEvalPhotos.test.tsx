import { act, fireEvent, screen, waitFor } from '@testing-library/react-native';
import { View, type ViewProps } from 'react-native';
import { setPictureFiles } from '@/features/scan/pictureFiles';
import { configureAuth, resetAuth } from '@/lib/auth/session';
import { MemoryNativeAuth } from '@/lib/auth/native';
import { evalsMock } from '@/mocks/evals';
import { renderWithProviders } from '@/test/render';
import { goldenPhotoApi } from './api';
import { AddPhotosController } from './addPhotos';
import { EvalsScreen } from './EvalsScreen';
import { makeKit } from './evalsTestKit';
import type { OriginalPickResult, PickedOriginal } from './originalPicker';

const mockSafeAreaView = ({ children }: ViewProps) => <View testID="safe-area">{children}</View>;
jest.mock('react-native-safe-area-context', () => ({
  SafeAreaView: (props: ViewProps) => mockSafeAreaView(props),
  SafeAreaProvider: ({ children }: ViewProps) => <>{children}</>,
  useSafeAreaInsets: () => ({ top: 0, bottom: 0, left: 0, right: 0 }),
}));

// The originals must reach the PC untouched: nothing in this flow may use the image manipulator.
jest.mock('expo-image-manipulator', () => {
  throw new Error('Adding eval photos must never run a picture through expo-image-manipulator.');
});

const IMG_1 = 'file:///cache/ImagePicker/IMG_0001.HEIC';
const IMG_2 = 'file:///cache/ImagePicker/IMG_0002.JPG';
const SIZES: Record<string, number> = { [IMG_1]: 1234, [IMG_2]: 5678 };

const original = (uri: string): PickedOriginal => ({ uri, width: 4032, height: 3024, fileName: null, mimeType: null, fileSize: null });

function photosController(result: OriginalPickResult = { status: 'picked', photos: [original(IMG_1), original(IMG_2)] }) {
  const read = jest.fn(async (uri: string) => new Blob([new Uint8Array(SIZES[uri] ?? 10)]));
  const controller = new AddPhotosController({ picker: { pick: async () => result }, read, api: goldenPhotoApi });
  return { controller, read };
}

async function open(controller: AddPhotosController, prepare?: () => void) {
  const kit = makeKit({ photos: 1 });
  prepare?.(); // after the kit, which sets the PC's list
  await renderWithProviders(<EvalsScreen runner={kit.runner} photos={controller} />);
  await act(async () => {
    fireEvent.press(screen.getByRole('button', { name: 'Add photos' }));
  });
  await screen.findByLabelText('Name for photo 1');
}

const nameOf = (n: number) => screen.getByLabelText(`Name for photo ${n}`).props.value as string;

beforeEach(() => {
  evalsMock.reset();
  configureAuth({ platform: 'native', native: new MemoryNativeAuth() });
});
afterEach(() => {
  resetAuth();
  setPictureFiles(undefined);
});

describe('Add photos on the Evals screen', () => {
  it('says once, plainly, that the photos go to the PC\'s eval folder and nowhere else', async () => {
    const { controller } = photosController();
    const kit = makeKit({ photos: 1 });
    await renderWithProviders(<EvalsScreen runner={kit.runner} photos={controller} />);

    expect(screen.getAllByText(/nowhere else/)).toHaveLength(1);
    expect(screen.getByText(/eval folder, at full size, and nowhere else/)).toBeTruthy();
  });

  it('opens the library from the Add photos button and shows a suggested name for each photo, free on the PC', async () => {
    const { controller } = photosController();
    await open(controller);

    expect(nameOf(1)).toBe('cover-01');
    expect(nameOf(2)).toBe('cover-02');
    expect(screen.getByRole('button', { name: 'Send 2 photos to your PC' })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Cover for photo 1' }).props.accessibilityState.selected).toBe(true);
    expect(screen.getByRole('button', { name: 'Shelf for photo 1' }).props.accessibilityState.selected).toBe(false);
  });

  it('suggests a shelf name when Shelf is chosen, and lets the name be edited', async () => {
    const { controller } = photosController();
    await open(controller);

    await act(async () => {
      fireEvent.press(screen.getByRole('button', { name: 'Shelf for photo 2' }));
    });
    await act(async () => {
      fireEvent.changeText(screen.getByLabelText('Name for photo 1'), 'front-cover');
    });

    expect(nameOf(1)).toBe('front-cover');
    expect(nameOf(2)).toBe('shelf-01');
  });

  it('sends each photo once to the PC with its set and name, and says what was added', async () => {
    const { controller, read } = photosController();
    await open(controller);
    await act(async () => {
      fireEvent.press(screen.getByRole('button', { name: 'Shelf for photo 2' }));
    });

    await act(async () => {
      fireEvent.press(screen.getByRole('button', { name: 'Send 2 photos to your PC' }));
    });

    expect(await screen.findByText("Added 2 photos to your PC's eval folder.")).toBeTruthy();
    expect(evalsMock.uploads).toEqual([
      { set: 'single', id: 'cover-01', contentType: 'application/octet-stream', size: 1234 },
      { set: 'shelf', id: 'shelf-01', contentType: 'application/octet-stream', size: 5678 },
    ]);
    expect(read.mock.calls.map(([uri]) => uri)).toEqual([IMG_1, IMG_2]);
    expect(screen.getAllByText('Added')).toHaveLength(2);
    expect(screen.queryByRole('button', { name: /^Send \d+ photos? to your PC$/ })).toBeNull();
  });

  it('shows the photo on its way while it is sent', async () => {
    const { controller } = photosController();
    await open(controller);
    const gate: { open: () => void } = { open: () => {} };
    const hold = new Promise<void>((resolve) => {
      gate.open = resolve;
    });
    const sendOne = jest.spyOn(goldenPhotoApi, 'add').mockImplementation(async (set, id, photo) => {
      await hold;
      return { id, set, hasTruth: false, bytes: photo.size };
    });
    try {
      await act(async () => {
        fireEvent.press(screen.getByRole('button', { name: 'Send 2 photos to your PC' }));
      });

      expect(await screen.findByRole('button', { name: 'Sending photo 1 of 2' })).toBeTruthy();
      expect(screen.getByRole('button', { name: 'Start the run' }).props.accessibilityState.disabled).toBe(true);
      await act(async () => {
        gate.open();
      });
      expect(await screen.findByText("Added 2 photos to your PC's eval folder.")).toBeTruthy();
    } finally {
      sendOne.mockRestore();
    }
  });

  it('shows "name taken" when the PC already has the name, offers a new one and lets it be renamed', async () => {
    evalsMock.failUpload['cover-01'] = 409;
    const { controller } = photosController({ status: 'picked', photos: [original(IMG_1)] });
    await open(controller);

    await act(async () => {
      fireEvent.press(screen.getByRole('button', { name: 'Send 1 photo to your PC' }));
    });

    expect(await screen.findByText('cover-01 is taken. Try cover-02, or type another name.')).toBeTruthy();
    expect(nameOf(1)).toBe('cover-02');

    // The owner would rather pick their own name.
    await act(async () => {
      fireEvent.changeText(screen.getByLabelText('Name for photo 1'), 'my-cover');
    });
    expect(screen.queryByText(/is taken/)).toBeNull();
    await act(async () => {
      fireEvent.press(screen.getByRole('button', { name: 'Send 1 photo to your PC' }));
    });

    expect(await screen.findByText("Added 1 photo to your PC's eval folder.")).toBeTruthy();
    expect(evalsMock.uploads.map((u) => u.id)).toEqual(['cover-01', 'my-cover']);
  });

  it('refuses a name that is already on the PC before sending anything', async () => {
    const { controller } = photosController({ status: 'picked', photos: [original(IMG_1)] });
    await open(controller, () => {
      evalsMock.photos = [{ id: 'my-cover', set: 'shelf', hasTruth: false, bytes: 1 }];
    });

    await act(async () => {
      fireEvent.changeText(screen.getByLabelText('Name for photo 1'), 'My-Cover');
    });

    expect(screen.getByText('That name is already used. Pick another.')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Send 1 photo to your PC' }).props.accessibilityState.disabled).toBe(true);
    expect(evalsMock.uploads).toEqual([]);
  });

  it('says a photo is too large when the PC refuses it, and a photo that failed can be sent again', async () => {
    evalsMock.failUpload['cover-01'] = 413;
    evalsMock.failUpload['cover-02'] = 500;
    const { controller } = photosController();
    await open(controller);

    await act(async () => {
      fireEvent.press(screen.getByRole('button', { name: 'Send 2 photos to your PC' }));
    });

    expect(await screen.findByText('Too large. The limit is 40 MB.')).toBeTruthy();
    expect(screen.getByText("Couldn't send it to your PC. Try again.")).toBeTruthy();
    const retry = screen.getByRole('button', { name: 'Send 1 photo to your PC' });
    await act(async () => {
      fireEvent.press(retry);
    });
    expect(await screen.findByText("Added 1 photo to your PC's eval folder.")).toBeTruthy();
    expect(evalsMock.uploads.map((u) => u.id)).toEqual(['cover-01', 'cover-02', 'cover-02']);
    // The one that is too large stays on the list until it is removed.
    expect(screen.getByText(/can't be sent/)).toBeTruthy();
    await act(async () => {
      fireEvent.press(screen.getByRole('button', { name: 'Remove photo 1' }));
    });
    expect(screen.queryByText('Too large. The limit is 40 MB.')).toBeNull();
  });

  it('tells the owner when photo access is off', async () => {
    const { controller } = photosController({ status: 'denied' });
    const kit = makeKit({ photos: 1 });
    await renderWithProviders(<EvalsScreen runner={kit.runner} photos={controller} />);

    await act(async () => {
      fireEvent.press(screen.getByRole('button', { name: 'Add photos' }));
    });

    expect(await screen.findByText('Photos access is off. Turn it on in Settings to add photos.')).toBeTruthy();
  });

  it('keeps the Add photos card off the screen while a run is going', async () => {
    const { controller } = photosController();
    const kit = makeKit({ photos: 1, onRead: () => new Promise(() => {}) });
    await renderWithProviders(<EvalsScreen runner={kit.runner} photos={controller} />);
    expect(screen.getByRole('button', { name: 'Add photos' })).toBeTruthy();

    await act(async () => {
      fireEvent.press(screen.getByRole('button', { name: 'Start the run' }));
    });

    await waitFor(() => expect(screen.queryByRole('button', { name: 'Add photos' })).toBeNull());
    expect(screen.queryByText('Add test photos')).toBeNull();
  });
});
