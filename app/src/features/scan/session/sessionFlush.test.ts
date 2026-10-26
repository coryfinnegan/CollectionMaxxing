// The reconnect order: barcode batches first, then identify payloads in shoot order (docs/16 §9).
import { waitFor } from '@testing-library/react-native';
import { MOCK_LIBRARY_ID } from '@/mocks/data';
import { identifyMock } from '@/mocks/identify';
import { scanMock } from '@/mocks/scan';
import { useCardsStore } from '../cards/cardsStore';
import { SnapPipeline } from '../pipeline/SnapPipeline';
import { makeKit, readingOf, readItem, resetKit } from '../pipeline/testKit';
import { createTrayStore, flushTray, MemoryKeyValueStore, type FlushDeps } from '../tray/trayStore';
import { flushSession } from './sessionFlush';
import { flushDeps } from './sessionApi';

const dune = readItem('Dune');
const mario = readItem('Super Mario 64', { kind: 'game', platform: 'Nintendo 64' });

const readSnap = (snapId: string, createdAt: number, localId: string) => ({
  snapId,
  libraryId: MOCK_LIBRARY_ID,
  trigger: 'shutter' as const,
  createdAt,
  thumbUri: 'file:///snaps/x-still.jpg',
  phase: 'read' as const,
  sent: false,
  items: [{ localId, text: localId, title: localId, payload: { localId } as never }],
  unreadableCount: 0,
});

beforeEach(() => {
  scanMock.reset();
  identifyMock.reset();
  useCardsStore.getState().reset();
});
afterEach(() => {
  resetKit();
  scanMock.reset();
});

describe('flushSession', () => {
  it('sends barcode batches before identify payloads', async () => {
    const order: string[] = [];
    const tray = createTrayStore({ storage: new MemoryKeyValueStore(), now: () => 1_000 });
    tray.getState().scan({ value: 'code-1', symbology: 'ean-13' });
    const trayDeps: FlushDeps = {
      createSession: async () => 'session-1',
      sendBatch: async (_sessionId, batch) => void order.push(`barcodes:${batch.entries.map((e) => e.value).join(',')}`),
      classify: () => ({ offline: true }),
    };
    const pipeline = { flush: async () => void order.push('identify') };

    await flushSession({ tray, trayDeps, pipeline });

    expect(order).toEqual(['barcodes:code-1', 'identify']);
  });
});

describe('the identify gate', () => {
  it('holds identify while the tray still holds unsent codes, then sends in shoot order', async () => {
    const kit = makeKit();
    kit.tray.getState().scan({ value: 'code-1', symbology: 'ean-13' });
    // Stored newest-first on purpose: the send order must still be oldest first.
    kit.store.getState().addSnap(readSnap('snap-new', 2, 'new-item'));
    kit.store.getState().addSnap(readSnap('snap-old', 1, 'old-item'));
    const pipeline = new SnapPipeline(kit.deps);

    await pipeline.flush();
    expect(identifyMock.requests).toHaveLength(0);

    // The tray's own flush sends the codes; the next pipeline flush sends the pictures, oldest first.
    await flushTray(kit.tray, flushDeps(MOCK_LIBRARY_ID));
    expect(kit.tray.getState().lines[0]).toMatchObject({ sentCopies: 1 });
    await pipeline.flush();
    await waitFor(() => expect(identifyMock.requests).toHaveLength(2));
    expect(identifyMock.requests.map((r) => r.body.items?.[0]?.localId)).toEqual(['old-item', 'new-item']);
    pipeline.stop();
  });

  it('does not let a tray problem hold pictures up', async () => {
    const kit = makeKit();
    kit.tray.getState().scan({ value: 'code-1', symbology: 'ean-13' });
    kit.tray.getState().setOnline(true, 'A batch was refused.');
    kit.store.getState().addSnap(readSnap('snap-1', 1, 'only-item'));
    const pipeline = new SnapPipeline(kit.deps);

    await pipeline.flush();
    await waitFor(() => expect(identifyMock.requests).toHaveLength(1));
    pipeline.stop();
  });
});

describe('pinning from the pipeline', () => {
  it('pins a snap while it is queued and unpins it once it leaves', async () => {
    const kit = makeKit();
    kit.reader.script(readingOf([dune, mario]));
    const pipeline = new SnapPipeline(kit.deps);
    pipeline.start();

    await pipeline.onSnap(await kit.snapEvent());
    const [snap] = kit.store.getState().snaps;
    expect(kit.snaps.isPinned(snap!.snapId)).toBe(true);

    kit.tray.getState().setSessionId(null); // the scan ended: its snaps leave the receipt
    pipeline.sweep();
    expect(kit.store.getState().snaps).toHaveLength(0);
    expect(kit.snaps.isPinned(snap!.snapId)).toBe(false);
    pipeline.stop();
  });
});
