import type { ModelItem, ReadItemsOptions, ReadItemsResult } from '../types';

type Listener = (item: ModelItem) => void;

/** A stand-in for the native CmVision module: Swift emits `onItem` events, then resolves. */
const mockNative = {
  listeners: [] as Listener[],
  removed: 0,
  readItems: jest.fn<Promise<ReadItemsResult>, [string, ReadItemsOptions]>(),
  modelCapabilities: jest.fn(),
  recognizeText: jest.fn(),
  deviceStatus: jest.fn(),
  addListener: jest.fn((event: string, listener: Listener) => {
    expect(event).toBe('onItem');
    mockNative.listeners.push(listener);
    return {
      remove: () => {
        mockNative.removed += 1;
        mockNative.listeners = mockNative.listeners.filter((l) => l !== listener);
      },
    };
  }),
  emit(item: ModelItem) {
    for (const listener of [...mockNative.listeners]) listener(item);
  },
};

jest.mock('expo', () => ({ requireNativeModule: () => mockNative }));
jest.mock('react-native-vision-camera', () => ({ useObjectOutput: jest.fn() }));

// eslint-disable-next-line @typescript-eslint/no-require-imports
const { visionEngine } = require('../index') as typeof import('../index');

const options: ReadItemsOptions = { prompt: 'p', timeoutMs: 5000, maxItems: 10 };
const item = (order: number, title: string): ModelItem => ({
  visibleText: title,
  title,
  kind: 'book',
  order,
  confidence: 'high',
});

beforeEach(() => {
  mockNative.listeners = [];
  mockNative.removed = 0;
  mockNative.addListener.mockClear();
  mockNative.readItems.mockReset();
  mockNative.modelCapabilities.mockReset();
  mockNative.recognizeText.mockReset();
});

describe('the native wrapper', () => {
  it('passes each streamed item to onItem in the order Swift emitted it, then resolves once', async () => {
    const streamed = [item(0, 'First'), item(1, 'Second'), item(2, 'Third')];
    const result: ReadItemsResult = { items: streamed, unreadableCount: 0, outcome: 'ok', elapsedMs: 900 };
    mockNative.readItems.mockImplementation(async () => {
      for (const streamedItem of streamed) mockNative.emit(streamedItem);
      return result;
    });
    const received: ModelItem[] = [];
    const resolved = jest.fn();

    const promise = visionEngine.readItems('file:///a.jpg', options, (i) => received.push(i)).then(resolved);
    await promise;

    expect(received.map((i) => i.title)).toEqual(['First', 'Second', 'Third']);
    expect(resolved).toHaveBeenCalledTimes(1);
    expect(mockNative.readItems).toHaveBeenCalledTimes(1);
    expect(mockNative.readItems).toHaveBeenCalledWith('file:///a.jpg', options);
  });

  it('subscribes before the native call starts, so no early item is missed', async () => {
    const order: string[] = [];
    mockNative.addListener.mockImplementationOnce((_event: string, listener: Listener) => {
      order.push('subscribe');
      mockNative.listeners.push(listener);
      return { remove: () => undefined };
    });
    mockNative.readItems.mockImplementation(async () => {
      order.push('read');
      return { items: [], unreadableCount: 0, outcome: 'ok', elapsedMs: 1 };
    });
    await visionEngine.readItems('file:///a.jpg', options, () => undefined);
    expect(order).toEqual(['subscribe', 'read']);
  });

  it('removes its listener after the read, even when the native call throws', async () => {
    mockNative.readItems.mockRejectedValue(new Error('boom'));
    await visionEngine.readItems('file:///a.jpg', options, () => undefined);
    expect(mockNative.removed).toBe(1);
    expect(mockNative.listeners).toEqual([]);
  });

  it('resolves as unavailable, never throws, when the native read throws', async () => {
    mockNative.readItems.mockRejectedValue(new Error('boom'));
    await expect(visionEngine.readItems('file:///a.jpg', options)).resolves.toEqual({
      items: [],
      unreadableCount: 0,
      outcome: 'unavailable',
      elapsedMs: 0,
    });
  });

  it.each(['declined', 'timeout', 'contextExceeded', 'unavailable'] as const)(
    'returns the %s outcome with the items streamed before it',
    async (outcome) => {
      const partial = [item(0, 'Before')];
      mockNative.readItems.mockImplementation(async () => {
        mockNative.emit(partial[0]);
        return { items: partial, unreadableCount: 1, outcome, elapsedMs: 4000 };
      });
      const received: ModelItem[] = [];
      const result = await visionEngine.readItems('file:///a.jpg', options, (i) => received.push(i));
      expect(result.outcome).toBe(outcome);
      expect(result.items).toEqual(partial);
      expect(received).toEqual(partial);
    },
  );

  it('does not subscribe when the caller passes no onItem', async () => {
    mockNative.readItems.mockResolvedValue({ items: [], unreadableCount: 0, outcome: 'ok', elapsedMs: 1 });
    await visionEngine.readItems('file:///a.jpg', options);
    expect(mockNative.addListener).not.toHaveBeenCalled();
  });

  it('reports the model as not supported when the capability call throws', async () => {
    mockNative.modelCapabilities.mockRejectedValue(new Error('no module'));
    await expect(visionEngine.modelCapabilities()).resolves.toEqual({
      available: false,
      vision: false,
      guidedGeneration: false,
      contextSize: null,
      reason: 'notSupported',
    });
  });

  it('sends orientation [0] to Swift by default and the requested orientations otherwise', async () => {
    mockNative.recognizeText.mockResolvedValue([]);
    await visionEngine.recognizeText('file:///a.jpg');
    await visionEngine.recognizeText('file:///a.jpg', { orientations: [0, 90, 270] });
    expect(mockNative.recognizeText.mock.calls).toEqual([
      ['file:///a.jpg', [0]],
      ['file:///a.jpg', [0, 90, 270]],
    ]);
  });

  describe('overlapping readItems calls run one at a time', () => {
    const okResult: ReadItemsResult = { items: [], unreadableCount: 0, outcome: 'ok', elapsedMs: 1 };
    const deferred = () => {
      let resolve!: (r: ReadItemsResult) => void;
      let reject!: (e: Error) => void;
      const promise = new Promise<ReadItemsResult>((res, rej) => {
        resolve = res;
        reject = rej;
      });
      return { promise, resolve, reject };
    };
    const flush = () => new Promise((r) => setTimeout(r, 0));

    it('starts the second native call only after the first resolves', async () => {
      const first = deferred();
      const second = deferred();
      mockNative.readItems.mockReturnValueOnce(first.promise).mockReturnValueOnce(second.promise);

      const a = visionEngine.readItems('file:///a.jpg', options);
      const b = visionEngine.readItems('file:///b.jpg', options);
      await flush();
      expect(mockNative.readItems).toHaveBeenCalledTimes(1);

      first.resolve(okResult);
      await a;
      await flush();
      expect(mockNative.readItems).toHaveBeenCalledTimes(2);
      second.resolve(okResult);
      await b;
    });

    it('gives each caller only its own items', async () => {
      mockNative.readItems.mockImplementation(async (uri: string) => {
        mockNative.emit(item(0, uri));
        await flush();
        return okResult;
      });
      const got: Record<string, string[]> = { a: [], b: [] };
      await Promise.all([
        visionEngine.readItems('A', options, (i) => got.a.push(i.title)),
        visionEngine.readItems('B', options, (i) => got.b.push(i.title)),
      ]);
      expect(got).toEqual({ a: ['A'], b: ['B'] });
    });

    it('does not let a failed call block the next one', async () => {
      const first = deferred();
      mockNative.readItems.mockReturnValueOnce(first.promise).mockResolvedValueOnce(okResult);
      const a = visionEngine.readItems('file:///a.jpg', options);
      const b = visionEngine.readItems('file:///b.jpg', options);
      first.reject(new Error('boom'));
      expect((await a).outcome).toBe('unavailable');
      expect((await b).outcome).toBe('ok');
      expect(mockNative.readItems).toHaveBeenCalledTimes(2);
    });
  });
});
