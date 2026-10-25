// LOCKED acceptance tests for CAM-3b (issue #118). Never weaken, skip or delete these.
import { QueryClientProvider } from '@tanstack/react-query';
import { act, fireEvent, render, screen, waitFor } from '@testing-library/react-native';
import { HttpResponse, http } from 'msw';
import { clearLocalData } from '@/features/me/localData';
import { MOCK_LIBRARY_ID } from '@/mocks/data';
import { CLOSE_CALL, identifyMock } from '@/mocks/identify';
import { reviewMock } from '@/mocks/scan-review';
import { scanMock } from '@/mocks/scan';
import { server } from '@/mocks/server';
import { Button } from '@/ui';
import { makeQueryClient, renderWithProviders } from '@/test/render';
import type { Resizer } from '../autosnap/snapPhoto';
import { useCardsStore } from '../cards/cardsStore';
import { CloseScannerSheet } from '../close/CloseScannerSheet';
import { useCloseScanner } from '../close/useCloseScanner';
import { DEV_CODES } from '../scanner/devCodes';
import { ScannerScreen } from '../scanner/ScannerScreen';
import { FakeScannerSource } from '../scanner/ScannerSource';
import { SnapPipeline } from './SnapPipeline';
import { SnapStoreBridge } from './SnapStoreBridge';
import { makeKit, PHOTO, readItem, readingOf, recordRequests, resetKit } from './testKit';

const mockRouter = { push: jest.fn(), back: jest.fn(), replace: jest.fn() };
jest.mock('expo-router', () => ({ useRouter: () => mockRouter }));

const resize: Resizer = async (photo, target) => ({ uri: `${photo.uri}#resized`, width: target?.width ?? photo.width, height: target?.height ?? photo.height });
const tuning = { retryMs: 20, pollMs: 20, resendMs: 40, flashMs: 50 };

const dune = readItem('Dune', { box: { x: 0.05, y: 0.1, width: 0.2, height: 0.7 } });
const mario = readItem('Super Mario 64', { kind: 'game', platform: 'Nintendo 64', box: { x: 0.3, y: 0.1, width: 0.2, height: 0.3 } });
const yellow = readItem(CLOSE_CALL.title, { kind: 'game', platform: 'Game Boy', box: { x: 0.55, y: 0.1, width: 0.2, height: 0.3 } });
const mutiny = readItem('Space Mutiny', { kind: 'film', format: 'vhs', box: { x: 0.8, y: 0.1, width: 0.15, height: 0.7 } });

const asRole = (role: 'viewer' | 'editor' | 'owner') => {
  scanMock.role = role;
  server.use(http.get('*/api/v1/me', () => HttpResponse.json({ id: 'u1', memberships: [{ libraryId: MOCK_LIBRARY_ID, role }] })));
};

const open = async (kit: ReturnType<typeof makeKit>, source = new FakeScannerSource()) => {
  await renderWithProviders(
    <ScannerScreen libraryId={MOCK_LIBRARY_ID} source={source} pipeline={kit.deps} tuning={tuning} autoSnapTuning={{ flashMs: 50, resize }} />,
  );
  return source;
};

const shutter = async () => {
  await fireEvent.press(screen.getByLabelText('Take picture'));
};

beforeEach(() => {
  scanMock.reset();
  identifyMock.reset();
  reviewMock.reset();
  useCardsStore.getState().reset();
});

afterEach(() => {
  resetKit();
  scanMock.reset();
});

describe('no image leaves the phone', () => {
  it('sends only JSON text across a full snap: the read, identify and the art step', async () => {
    const kit = makeKit();
    kit.reader.script(readingOf([dune, mario, yellow, mutiny]));
    kit.art.distances.set('0001', 0.1);
    kit.art.distances.set('0002', 0.7);
    const requests = recordRequests();
    await open(kit);

    await shutter();
    expect(await screen.findByText('Space Mutiny')).toBeTruthy();
    await waitFor(() => expect(identifyMock.artRequests).toHaveLength(1)); // the art step ran
    await waitFor(() => expect(kit.art.downloads).toHaveLength(2));

    const sent = (await requests.all()).filter((r) => r.method !== 'GET');
    requests.stop();
    expect(sent.map((r) => new URL(r.url).pathname.replace(/[0-9a-f-]{36}/g, ':id'))).toEqual(
      expect.arrayContaining(['/api/v1/scan-sessions/:id/identify', '/api/v1/scan-sessions/:id/candidates/:id:art']),
    );
    for (const request of sent) {
      expect(request.contentType ?? '').toMatch(/^application\/json/); // never multipart, never an image
      expect(request.body).not.toMatch(/data:|file:|content:|base64|\.jpe?g|\.heic|\.png/i);
    }
    // The only photo upload is "Use my photo" (CAM-3c), and nothing here makes it.
    expect(sent.some((r) => /\/photo$/.test(new URL(r.url).pathname))).toBe(false);
    // What went out for the picture is the read text and fields, and the distances: numbers.
    expect(identifyMock.requests).toHaveLength(1);
    expect(identifyMock.requests[0]!.body.items?.map((i) => i.title)).toEqual(['Dune', 'Super Mario 64', 'Pokemon Yellow', 'Space Mutiny']);
    expect(identifyMock.artRequests[0]!.distances.every((d) => typeof d.distance === 'number')).toBe(true);
  });
});

describe('the snap store is wiped', () => {
  const snapOnce = async (kit: ReturnType<typeof makeKit>) => {
    kit.reader.script(readingOf([dune, mario]));
    await new SnapPipeline(kit.deps).onSnap({ photo: PHOTO, trigger: 'shutter', shotKind: 'shelf_segment' });
    await waitFor(() => expect(kit.snaps.list()[0]?.items.every((i) => i.cropUri)).toBe(true));
    expect(kit.snaps.list()).toHaveLength(1);
    expect(kit.files.files.size).toBeGreaterThan(2); // the still and the crops
    expect(kit.snaps.list()[0]!.items.map((i) => i.box)).toEqual([dune.box, mario.box]);
  };
  const empty = (kit: ReturnType<typeof makeKit>) => {
    expect(kit.snaps.list()).toEqual([]);
    expect(kit.files.files.size).toBe(0);
    expect(kit.store.getState().snaps).toEqual([]);
  };

  it('on Discard', async () => {
    const kit = makeKit();
    await snapOnce(kit);
    function Harness() {
      const close = useCloseScanner({ store: kit.tray, onLeave: jest.fn(), onReview: jest.fn() });
      return (
        <>
          <CloseScannerSheet state={close.state} unreviewed={close.unreviewed} onChoose={close.choose} onDismiss={close.dismiss} />
          <Button label="Close scanner" onPress={close.request} />
        </>
      );
    }
    await renderWithProviders(<Harness />);
    await fireEvent.press(screen.getByLabelText('Close scanner'));
    await fireEvent.press(await screen.findByLabelText('Discard'));
    await waitFor(() => empty(kit));
  });

  it('on sign-out and on unpair, through the registered clearers', async () => {
    const kit = makeKit();
    await snapOnce(kit);
    const client = makeQueryClient();
    const view = await render(
      <QueryClientProvider client={client}>
        <SnapStoreBridge />
      </QueryClientProvider>,
    );
    await clearLocalData(client); // what signing out and unpairing both run
    empty(kit);

    await snapOnce(kit);
    await clearLocalData(client);
    empty(kit);
    view.unmount();
  });
});

describe('nothing is added by scanning', () => {
  it('a snap, its cards and a close-call re-score create no items and accept no candidate', async () => {
    const kit = makeKit();
    kit.reader.script(readingOf([dune, mario, yellow, mutiny]));
    kit.art.distances.set('0001', 0.1);
    const requests = recordRequests();
    await open(kit);

    await shutter();
    expect(await screen.findByText('Space Mutiny')).toBeTruthy();
    await waitFor(() => expect(identifyMock.artRequests).toHaveLength(1));
    expect(await screen.findByText(CLOSE_CALL.title)).toBeTruthy();

    const seen = await requests.all();
    requests.stop();
    const writes = seen.filter((r) => r.method !== 'GET').map((r) => `${r.method} ${new URL(r.url).pathname.replace(/[0-9a-f-]{36}/g, ':id')}`);
    expect(new Set(writes)).toEqual(
      new Set(['POST /api/v1/libraries/:id/scan-sessions', 'POST /api/v1/scan-sessions/:id/identify', 'POST /api/v1/scan-sessions/:id/candidates/:id:art']),
    );
    const candidates = [...scanMock.sessions.values()].flatMap((s) => s.candidates);
    expect(candidates.length).toBe(4);
    expect(candidates.every((c) => c.decision === 'undecided' && c.createdItemId === null)).toBe(true);
    expect(reviewMock.commits).toEqual([]);
  });
});

describe('a barcode in view wins', () => {
  it('sends no picture for identification while a barcode is readable', async () => {
    const kit = makeKit();
    kit.reader.script(readingOf([dune]));
    const source = await open(kit);

    await act(async () => source.inject([{ value: DEV_CODES.duneOwned, symbology: 'ean-13' }]));
    await shutter();
    expect(await screen.findByText('Dune')).toBeTruthy(); // the barcode's card

    expect(kit.reader.stills).toEqual([]); // nothing was even read
    expect(identifyMock.requests).toEqual([]);
    expect(kit.store.getState().snaps).toEqual([]);
    expect(scanMock.barcodeRequests.map((r) => r.codes)).toEqual([[DEV_CODES.duneOwned]]);
  });
});

describe('a viewer', () => {
  it('can scan a picture and see ownership, but gets no Add buttons on picture cards', async () => {
    asRole('viewer');
    const kit = makeKit();
    kit.reader.script(readingOf([dune, mario, mutiny]));
    await open(kit);

    await shutter();
    expect(await screen.findByText('You own 1')).toBeTruthy();
    expect(await screen.findByText('Super Mario 64')).toBeTruthy();
    expect(await screen.findByText('Space Mutiny')).toBeTruthy();
    expect(screen.queryByLabelText('Add Super Mario 64')).toBeNull();
    expect(screen.queryByText('+ Add')).toBeNull();
    expect(screen.queryByText('Fix it')).toBeNull();
  });

  it('is the only difference: an editor gets Add and Fix it for the same snap', async () => {
    asRole('editor');
    const kit = makeKit();
    kit.reader.script(readingOf([dune, mario, mutiny]));
    await open(kit);

    await shutter();
    expect(await screen.findByLabelText('Add Super Mario 64')).toBeTruthy();
    expect(await screen.findByLabelText('Fix it: Space Mutiny')).toBeTruthy();
  });
});
