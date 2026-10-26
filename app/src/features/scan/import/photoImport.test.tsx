// Import from Photos on the scanner screen: the picker, the order, progress and cancel, barcodes, failures, and no files left.
import { act, fireEvent, screen, waitFor } from '@testing-library/react-native';
import { identifyMock } from '@/mocks/identify';
import { scanMock } from '@/mocks/scan';
import { useCardsStore } from '../cards/cardsStore';
import { discardAllPictures, trackedPictures } from '../pictureFiles';
import { DEV_CODES } from '../scanner/devCodes';
import { makeKit, readItem, readingOf, resetKit } from '../pipeline/testKit';
import { code, importKit } from './importKit';
import { DENIED_COPY } from './photoImport';

jest.mock('expo-router', () => ({ useRouter: () => ({ push: jest.fn(), back: jest.fn(), replace: jest.fn() }) }));

const read = (title: string) => readingOf([readItem(title)]);
const gate = () => {
  let release: () => void = () => undefined;
  const hold = new Promise<void>((resolve) => (release = resolve));
  return { hold, release };
};

beforeEach(() => {
  scanMock.reset();
  identifyMock.reset();
  useCardsStore.getState().reset();
});
afterEach(() => {
  resetKit();
  scanMock.reset();
  jest.restoreAllMocks();
});

describe('importing from Photos', () => {
  it('turns 3 picked images into 3 imports in order, asks for at most 20, and shows progress', async () => {
    const kit = makeKit();
    const { picker, open, pressPhotos } = importKit(kit);
    picker.images(3);
    kit.reader.script(read('Dune'), read('Space Mutiny'), read('Ghostbusters'));
    const hold = gate();
    kit.reader.hold = hold.hold;
    await open();

    await pressPhotos();
    expect(await screen.findByText('Reading 1 of 3')).toBeTruthy();
    expect(picker.asked).toEqual([20]);
    expect(screen.getByLabelText('Photos').props.accessibilityState.disabled).toBe(true);
    await act(async () => hold.release());

    await waitFor(() => expect(identifyMock.requests).toHaveLength(3));
    expect(identifyMock.requests.map((r) => r.body.trigger)).toEqual(['import', 'import', 'import']);
    expect(identifyMock.requests.map((r) => r.body.shotKind)).toEqual(['unknown', 'unknown', 'unknown']); // no live signals
    expect(identifyMock.requests.map((r) => r.body.items?.[0]?.title)).toEqual(['Dune', 'Space Mutiny', 'Ghostbusters']);
    await waitFor(() => expect(screen.queryByText(/^Reading \d of 3$/)).toBeNull());
  });

  it('stops the rest when you cancel', async () => {
    const kit = makeKit();
    const { picker, open, pressPhotos } = importKit(kit);
    picker.images(4);
    kit.reader.script(read('Dune'), read('Space Mutiny'), read('Ghostbusters'), read('Zelda'));
    const hold = gate();
    kit.reader.hold = hold.hold;
    await open();

    await pressPhotos();
    await screen.findByText('Reading 1 of 4');
    await fireEvent.press(screen.getByLabelText('Cancel the rest'));
    await act(async () => hold.release());

    expect(await screen.findByText('Stopped with 3 pictures left.')).toBeTruthy();
    expect(kit.reader.stills).toHaveLength(1);
    expect(kit.fs.strays()).toEqual([]); // the pictures never read are deleted too
  });

  it('sends a picture with a barcode down the barcode path and not to the reader', async () => {
    const kit = makeKit();
    const { picker, barcodes, open, pressPhotos } = importKit(kit);
    const [withCode] = picker.images(2);
    barcodes.codes.set(withCode as string, [code(DEV_CODES.duneOwned)]);
    kit.reader.script(read('Space Mutiny'));
    await open();

    await pressPhotos();
    expect(await screen.findByText('Dune')).toBeTruthy();
    await waitFor(() => expect(identifyMock.requests).toHaveLength(1));
    expect(kit.reader.stills).toHaveLength(1); // only the picture without a code was read
    expect(identifyMock.requests[0]?.body.items?.map((i) => i.title)).toEqual(['Space Mutiny']);
    expect(barcodes.checked).toHaveLength(2);
  });

  it('still reads a picture when the barcode check fails', async () => {
    const kit = makeKit();
    const { picker, barcodes, open, pressPhotos } = importKit(kit);
    picker.images(1);
    jest.spyOn(barcodes, 'detectBarcodes').mockRejectedValue(new Error('vision'));
    kit.reader.script(read('Dune'));
    await open();
    await pressPhotos();
    await waitFor(() => expect(identifyMock.requests).toHaveLength(1));
  });

  it('reports a picture with nothing in it or that cannot be read, and continues with the rest', async () => {
    const kit = makeKit();
    const { picker, open, pressPhotos } = importKit(kit);
    picker.images(4);
    const reads = kit.reader.read.bind(kit.reader);
    let n = 0;
    jest.spyOn(kit.reader, 'read').mockImplementation(async (still, onItem) => {
      n += 1;
      if (n === 2) throw new Error('unreadable file');
      return reads(still, onItem);
    });
    kit.reader.script(readingOf([]), read('Dune'));
    // read 1: nothing; read 2: throws; read 3: Dune; read 4: nothing (queue empty)
    await open();

    await pressPhotos();
    expect(await screen.findByText("Couldn't open 1 picture. Nothing to read in 2 pictures.")).toBeTruthy();
    await waitFor(() => expect(identifyMock.requests).toHaveLength(1));
    expect(kit.reader.stills).toHaveLength(3);
    await waitFor(() => expect(kit.fs.strays()).toEqual([]));
  });

  it('says so when photo access is denied, and nothing is read', async () => {
    const kit = makeKit();
    const { picker, open, pressPhotos } = importKit(kit);
    picker.picks.push({ status: 'denied' });
    await open();
    await pressPhotos();
    expect(await screen.findByText(DENIED_COPY)).toBeTruthy();
    expect(kit.reader.stills).toHaveLength(0);
    await fireEvent.press(screen.getByLabelText('Photos')); // and it can be tried again
    expect(picker.asked).toHaveLength(2);
  });

  it('does nothing when the picker is closed without a choice', async () => {
    const kit = makeKit();
    const { picker, open, pressPhotos } = importKit(kit);
    await open();
    await pressPhotos();
    await waitFor(() => expect(picker.asked).toHaveLength(1));
    expect(screen.queryByText(/Reading|Nothing to read|Couldn't/)).toBeNull();
  });
});

describe('picture files', () => {
  it('deletes the picked copy and the scaled copy once read; after Discard none remain', async () => {
    const kit = makeKit();
    const { picker, barcodes, open, pressPhotos } = importKit(kit);
    const uris = picker.images(5);
    barcodes.codes.set(uris[4] as string, [code(DEV_CODES.duneOwned)]);
    kit.reader.script(read('Space Mutiny'), read('Ghostbusters'), readingOf([]), read('Zelda'));
    await open();

    await pressPhotos();
    await waitFor(() => expect(kit.reader.stills).toHaveLength(4));
    await waitFor(() => expect(identifyMock.requests).toHaveLength(3));
    // Every picked file is gone as soon as it was read; what is left is the store's copies and the receipt's crops.
    await waitFor(() => expect(uris.filter((u) => kit.fs.live().includes(u))).toEqual([]));
    expect(kit.fs.created.filter((u) => u.includes('ImageManipulator/import-'))).toHaveLength(4);
    expect(kit.fs.live().filter((u) => u.includes('ImageManipulator/import-'))).toEqual([]);

    await discardAllPictures();
    expect(kit.fs.strays()).toEqual([]);
    expect(trackedPictures()).toEqual([]);
  });
});
