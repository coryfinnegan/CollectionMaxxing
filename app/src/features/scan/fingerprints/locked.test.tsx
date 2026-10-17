// LOCKED acceptance tests for CAM-3e (issue #121). Never weaken, skip or delete these.
import { waitFor } from '@testing-library/react-native';
import { HttpResponse, http } from 'msw';
import { MOCK_LIBRARY_ID } from '@/mocks/data';
import { identifyMock } from '@/mocks/identify';
import { scanMock } from '@/mocks/scan';
import { server } from '@/mocks/server';
import { useCardsStore } from '../cards/cardsStore';
import { SnapPipeline } from '../pipeline/SnapPipeline';
import { makeKit, readItem, readingOf, recordRequests, resetKit } from '../pipeline/testKit';
import { backupPrints, restBackup } from './backup';
import { FakePrintsServer } from './fakeServer';
import { createLocalMatch } from './matcher';
import { FakeVision, freshStore, printRow } from './testKit';

const dune = readItem('Dune', { box: { x: 0.05, y: 0.1, width: 0.2, height: 0.7 } });

beforeEach(() => {
  scanMock.reset();
  identifyMock.reset();
  useCardsStore.getState().reset();
});
afterEach(() => resetKit());

describe('a local match is a hint, not a decision', () => {
  it('only fills localMatchReleaseId: it never accepts a candidate, adds an item or saves a print by itself', async () => {
    const store = freshStore();
    const confirmed = printRow(7, { synced: true, print: '0' }); // every crop prints as 0 in FakeVision, so this one is a hit
    store.put(confirmed);
    const vision = new FakeVision();
    const kit = makeKit({ localMatch: createLocalMatch({ store, vision, maxDistance: () => 0.5 }) });
    kit.reader.script(readingOf([dune]));
    const requests = recordRequests();

    const pipeline = new SnapPipeline(kit.deps);
    await pipeline.onSnap(await kit.snapEvent());
    await waitFor(() => expect(identifyMock.requests).toHaveLength(1));
    await waitFor(() => expect(Object.keys(useCardsStore.getState().byId).length).toBeGreaterThan(0));

    // The hint went out with the identify request, and the server is the one that verifies it.
    expect(identifyMock.requests[0]!.body.items?.[0]?.localMatchReleaseId).toBe(confirmed.releaseId);
    // No candidate was decided, nothing was committed, no item was created.
    const sent = (await requests.all()).filter((r) => r.method !== 'GET');
    requests.stop();
    const paths = sent.map((r) => `${r.method} ${new URL(r.url).pathname.replace(/[0-9a-f-]{36}/g, ':id')}`);
    expect(paths.every((p) => /\/identify$|\/sessions?$|scan-sessions$/.test(p))).toBe(true);
    expect(paths.some((p) => /commit|PATCH|\/items/.test(p))).toBe(false);
    expect(Object.values(useCardsStore.getState().byId).every((c) => c.decision === 'undecided' && c.createdItemId === null)).toBe(true);
    // A hit does not write to the store either: only a confirmation does.
    expect(store.size).toBe(1);
    expect(store.get(confirmed.itemId)).toEqual(confirmed);
  });
});

describe('prints never leave the phone except through the backup call to the users own library', () => {
  it('sends only the opaque print, the ids, the revision and the time, to the feature-prints route of that library', async () => {
    const store = freshStore();
    const rows = [printRow(1), printRow(2)];
    rows.forEach((r) => store.put(r));
    const vision = new FakeVision().set('file:///crop.jpg', 1);
    await createLocalMatch({ store, vision })({ uri: 'file:///crop.jpg' }); // a match, which is purely local
    const pcs = new FakePrintsServer().use();
    const requests = recordRequests();

    const result = await backupPrints({ store, canEdit: true, remote: restBackup(MOCK_LIBRARY_ID) });
    const seen = await requests.all();
    requests.stop();

    expect(result.ok).toBe(true);
    // Everything that left the phone: one PUT to this library's feature-prints.
    expect(seen).toHaveLength(1);
    const [request] = seen;
    expect(request!.method).toBe('PUT');
    expect(new URL(request!.url).pathname).toBe(`/api/v1/libraries/${MOCK_LIBRARY_ID}/feature-prints`);
    expect(request!.contentType ?? '').toMatch(/^application\/json/);
    const body = JSON.parse(request!.body) as { prints: Record<string, unknown>[] };
    expect(Object.keys(body)).toEqual(['prints']);
    for (const print of body.prints) {
      expect(Object.keys(print).sort()).toEqual(['createdAt', 'data', 'itemId', 'releaseId', 'revision']);
    }
    // No image, crop, path or text.
    expect(request!.body).not.toMatch(/file:|content:|data:image|\.jpe?g|\.heic|\.png|title|text/i);
    expect(vision.printed).toEqual(['file:///crop.jpg']);
    expect(pcs.puts).toHaveLength(1);
  });

  it('is not sent anywhere else: a failing route leaves the prints on the phone and calls no other address', async () => {
    const store = freshStore();
    store.put(printRow(1));
    const other: string[] = [];
    server.use(
      http.put('*/api/v1/libraries/:libraryId/feature-prints', () => HttpResponse.json({ title: 'Down', status: 500 }, { status: 500 })),
      http.all('*', ({ request }) => {
        other.push(request.url);
        return HttpResponse.json({}, { status: 404 });
      }),
    );

    const result = await backupPrints({ store, canEdit: true, remote: restBackup(MOCK_LIBRARY_ID) });

    expect(result.ok).toBe(false);
    expect(other).toEqual([]);
    expect(store.unsynced()).toHaveLength(1);
  });
});
