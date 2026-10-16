import { DEFAULT_RECOGNITION_CONFIG as D, DEFAULT_RECOGNITION_SETTINGS, type RecognitionConfig, promptOf } from './defaults';
import { type ConfigFetchResult, RecognitionConfigStore } from './config';
import { EngineSelector } from './EngineSelector';
import { line, modelItem, okRead, RecordingVision, STILL, testIds } from './fakes';
import { MemoryKeyValueStore } from '@/features/scan/tray/trayStore';

const v2: RecognitionConfig = {
  ...D,
  version: 'v2',
  prompt: { template: 'Read every item on the shelf.', outputRules: 'Skip what you cannot read.' },
  engine: { modelTimeoutMs: 3000, maxItemsPerSnap: 10 },
};

function setup(responses: (ConfigFetchResult | Error)[], storage = new MemoryKeyValueStore()) {
  const etags: (string | undefined)[] = [];
  const queue = [...responses];
  const fetcher = async (etag?: string) => {
    etags.push(etag);
    const next = queue.shift();
    if (!next) throw new Error('offline');
    if (next instanceof Error) throw next;
    return next;
  };
  const store = new RecognitionConfigStore({ storage, fetcher });
  return { store, storage, etags };
}

describe('RecognitionConfigStore', () => {
  it('starts on the built-in defaults, so the app never waits on the network', () => {
    expect(setup([]).store.current()).toEqual(DEFAULT_RECOGNITION_SETTINGS);
  });

  it('replaces the server-owned values when the version changes, and keeps local tuning', async () => {
    const { store } = setup([{ status: 'ok', etag: '"e2"', body: v2 }]);
    expect(await store.refresh()).toBe('updated');
    expect(store.current().version).toBe('v2');
    expect(store.current().prompt).toEqual(v2.prompt);
    expect(store.current().engine).toEqual(v2.engine);
    expect(store.current().local).toEqual(DEFAULT_RECOGNITION_SETTINGS.local);
  });

  it('gives the next readItems call the new prompt and limits', async () => {
    const { store } = setup([{ status: 'ok', etag: '"e2"', body: v2 }]);
    const vision = new RecordingVision({ [STILL.uri]: { read: okRead([modelItem('Dune', 'Dune', 0)]), text: [line('Dune', 0.1, 0.1, 0.1, 0.5)] } });
    const selector = new EngineSelector({ vision, settings: () => store.current(), newId: testIds });
    await selector.read(STILL, () => {});
    await store.refresh();
    await selector.read(STILL, () => {});
    expect(vision.readCalls[0].options.prompt).toBe(promptOf(D));
    expect(vision.readCalls[1].options).toEqual({ prompt: promptOf(v2), timeoutMs: 3000, maxItems: 10 });
  });

  it('sends If-None-Match with the cached tag and keeps the cache on 304', async () => {
    const { store, etags } = setup([{ status: 'ok', etag: '"e2"', body: v2 }, { status: 'notModified' }]);
    await store.refresh();
    expect(await store.refresh()).toBe('unchanged');
    expect(etags).toEqual([undefined, '"e2"']);
    expect(store.current().version).toBe('v2');
  });

  it.each([
    ['not an object', 'oops'],
    ['no version', { ...v2, version: undefined }],
    ['an empty prompt', { ...v2, prompt: { template: '', outputRules: 'x' } }],
    ['a negative timeout', { ...v2, engine: { modelTimeoutMs: -1, maxItemsPerSnap: 10 } }],
    ['a missing section', { version: 'v3', prompt: v2.prompt }],
  ])('keeps the last good config when the response is malformed (%s)', async (_name, body) => {
    const { store } = setup([{ status: 'ok', etag: '"e2"', body: v2 }, { status: 'ok', etag: '"e3"', body }]);
    await store.refresh();
    expect(await store.refresh()).toBe('kept');
    expect(store.current().version).toBe('v2');
  });

  it('keeps the last good config when the fetch fails, without throwing', async () => {
    const { store } = setup([{ status: 'ok', etag: '"e2"', body: v2 }, new Error('network down')]);
    await store.refresh();
    expect(await store.refresh()).toBe('kept');
    expect(store.current().version).toBe('v2');
  });

  it('uses the cached config offline after a restart', async () => {
    const first = setup([{ status: 'ok', etag: '"e2"', body: v2 }]);
    await first.store.refresh();
    const restarted = setup([new Error('offline')], first.storage);
    expect(restarted.store.current().version).toBe('v2');
    expect(await restarted.store.refresh()).toBe('kept');
    expect(restarted.store.current().prompt).toEqual(v2.prompt);
  });

  it('falls back to the defaults when the stored copy is damaged', () => {
    const storage = new MemoryKeyValueStore();
    storage.set('recognition-config-v1', '{not json');
    expect(setup([], storage).store.current()).toEqual(DEFAULT_RECOGNITION_SETTINGS);
  });

  it('shares one request between refreshes that overlap', async () => {
    const { store, etags } = setup([{ status: 'ok', etag: '"e2"', body: v2 }]);
    await Promise.all([store.refresh(), store.refresh()]);
    expect(etags).toHaveLength(1);
  });
});
