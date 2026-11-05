import type { ModelItem, ReadItemsOptions, ReadItemsResult, Rect, TextLine } from '../types';

type Listener = (item: ModelItem) => void;

/** A stand-in for the native CmVision module: Swift emits `onItem` events, then resolves. */
const mockNative = {
  listeners: [] as Listener[],
  removed: 0,
  readItems: jest.fn<Promise<ReadItemsResult>, [string, ReadItemsOptions]>(),
  extractItems: jest.fn<Promise<ReadItemsResult>, [string, ReadItemsOptions]>(),
  modelCapabilities: jest.fn(),
  recognizeText: jest.fn(),
  detectRectangles: jest.fn(),
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
  mockNative.extractItems.mockReset();
  mockNative.modelCapabilities.mockReset();
  mockNative.recognizeText.mockReset();
  mockNative.detectRectangles.mockReset();
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

  describe('recognizeText', () => {
    const quarter: Rect = { x: 0.5, y: 0, width: 0.5, height: 0.5 };

    it('sends orientation [0] and no options to Swift by default, so the request stays what the scanner always asked: accurate, no correction, the whole picture', async () => {
      mockNative.recognizeText.mockResolvedValue([]);
      await visionEngine.recognizeText('file:///a.jpg');
      await visionEngine.recognizeText('file:///a.jpg', {});
      expect(mockNative.recognizeText.mock.calls).toEqual([
        ['file:///a.jpg', [0], null],
        ['file:///a.jpg', [0], null],
      ]);
    });

    it('sends the requested orientations and, for the scanner read, still no options', async () => {
      mockNative.recognizeText.mockResolvedValue([]);
      await visionEngine.recognizeText('file:///a.jpg', { orientations: [0, 90, 270] });
      expect(mockNative.recognizeText.mock.calls).toEqual([['file:///a.jpg', [0, 90, 270], null]]);
    });

    it('sends 180 to Swift when the caller asks for upside-down text', async () => {
      mockNative.recognizeText.mockResolvedValue([]);
      await visionEngine.recognizeText('file:///a.jpg', { orientations: [180] });
      expect(mockNative.recognizeText.mock.calls).toEqual([['file:///a.jpg', [180], null]]);
    });

    it('sends the options the caller names as they were given, and only those', async () => {
      mockNative.recognizeText.mockResolvedValue([]);
      await visionEngine.recognizeText('file:///a.jpg', { level: 'fast' });
      await visionEngine.recognizeText('file:///a.jpg', { languageCorrection: true });
      await visionEngine.recognizeText('file:///a.jpg', { languageCorrection: false, level: 'accurate' });
      await visionEngine.recognizeText('file:///a.jpg', { orientations: [0], level: 'fast', languageCorrection: true, regions: [quarter] });
      expect(mockNative.recognizeText.mock.calls).toEqual([
        ['file:///a.jpg', [0], { level: 'fast' }],
        ['file:///a.jpg', [0], { languageCorrection: true }],
        ['file:///a.jpg', [0], { level: 'accurate', languageCorrection: false }],
        ['file:///a.jpg', [0], { level: 'fast', languageCorrection: true, regions: [quarter] }],
      ]);
    });

    it('sends no regions for an empty list, which is the whole picture', async () => {
      mockNative.recognizeText.mockResolvedValue([]);
      await visionEngine.recognizeText('file:///a.jpg', { regions: [] });
      expect(mockNative.recognizeText.mock.calls).toEqual([['file:///a.jpg', [0], null]]);
    });

    it('returns the lines Swift read, with their pass, unchanged when no region was asked for', async () => {
      const lines: TextLine[] = [
        { text: 'DUNE', confidence: 0.9, box: { x: 0.1, y: 0.1, width: 0.2, height: 0.1 }, pass: 0 },
        { text: 'ENUD', confidence: 0.2, box: { x: 0.1, y: 0.3, width: 0.2, height: 0.1 }, pass: 180 },
      ];
      mockNative.recognizeText.mockResolvedValue(lines);
      await expect(visionEngine.recognizeText('file:///a.jpg', { orientations: [0, 180] })).resolves.toEqual(lines);
    });

    it('puts the boxes Swift read inside each region back on the whole picture', async () => {
      mockNative.recognizeText.mockResolvedValue([{ text: 'DUNE', confidence: 0.9, box: { x: 0.4, y: 0.4, width: 0.2, height: 0.2 }, pass: 0, region: 1 }]);
      const lines = await visionEngine.recognizeText('file:///a.jpg', { regions: [{ x: 0, y: 0, width: 0.5, height: 0.5 }, quarter] });
      expect(lines).toHaveLength(1);
      expect(lines[0]).toMatchObject({ text: 'DUNE', pass: 0, region: 1 });
      expect(lines[0].box.x).toBeCloseTo(0.7);
      expect(lines[0].box.y).toBeCloseTo(0.2);
      expect(lines[0].box.width).toBeCloseTo(0.1);
      expect(lines[0].box.height).toBeCloseTo(0.1);
    });

    it('does not ask Swift to read regions in a turned pass, which it cannot place', async () => {
      await expect(visionEngine.recognizeText('file:///a.jpg', { orientations: [0, 90], regions: [quarter] })).rejects.toThrow(/upright/);
      expect(mockNative.recognizeText).not.toHaveBeenCalled();
    });
  });

  describe('detectRectangles', () => {
    const rect = { x: 0.1, y: 0.1, width: 0.5, height: 0.8 };

    it('sends no options to Swift when the caller gives none, so the request stays the shelf splitter\'s own', async () => {
      mockNative.detectRectangles.mockResolvedValue([rect]);
      await expect(visionEngine.detectRectangles('file:///a.jpg')).resolves.toEqual([rect]);
      expect(mockNative.detectRectangles.mock.calls).toEqual([['file:///a.jpg', null]]);
    });

    it('sends the options the caller gives, so a cover can ask for a case as wide as it is tall', async () => {
      mockNative.detectRectangles.mockResolvedValue([rect]);
      await visionEngine.detectRectangles('file:///a.jpg', { maximumAspectRatio: 1, minimumSize: 0.3 });
      expect(mockNative.detectRectangles.mock.calls).toEqual([['file:///a.jpg', { maximumAspectRatio: 1, minimumSize: 0.3 }]]);
    });
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

  describe('extractItems (spike #505)', () => {
    const okResult: ReadItemsResult = { items: [], unreadableCount: 0, outcome: 'ok', elapsedMs: 1 };

    it('sends the OCR text and options to Swift and streams items like readItems', async () => {
      mockNative.extractItems.mockImplementation(async () => {
        mockNative.emit(item(0, 'First'));
        return { items: [item(0, 'First')], unreadableCount: 0, outcome: 'ok', elapsedMs: 400 };
      });
      const received: ModelItem[] = [];
      const result = await visionEngine.extractItems('Dune\nFrank Herbert', options, (i) => received.push(i));
      expect(mockNative.extractItems).toHaveBeenCalledWith('Dune\nFrank Herbert', options);
      expect(result.outcome).toBe('ok');
      expect(received.map((i) => i.title)).toEqual(['First']);
    });

    it('resolves as unavailable, never throws, when the native call throws', async () => {
      mockNative.extractItems.mockRejectedValue(new Error('boom'));
      await expect(visionEngine.extractItems('Dune', options)).resolves.toEqual({
        items: [],
        unreadableCount: 0,
        outcome: 'unavailable',
        elapsedMs: 0,
      });
    });

    it('shares the queue with readItems: no two native model calls overlap', async () => {
      let resolveFirst!: (r: ReadItemsResult) => void;
      const first = new Promise<ReadItemsResult>((res) => {
        resolveFirst = res;
      });
      mockNative.readItems.mockReturnValueOnce(first);
      mockNative.extractItems.mockResolvedValueOnce(okResult);
      const a = visionEngine.readItems('file:///a.jpg', options);
      const b = visionEngine.extractItems('Dune', options);
      await new Promise((r) => setTimeout(r, 0));
      expect(mockNative.readItems).toHaveBeenCalledTimes(1);
      expect(mockNative.extractItems).not.toHaveBeenCalled();
      resolveFirst(okResult);
      await a;
      await b;
      expect(mockNative.extractItems).toHaveBeenCalledTimes(1);
    });
  });
});
