// How a finished export reaches the person. The download needs the sign-in, and the iPhone's is a bearer token a share target cannot
// send, so the phone fetches the zip itself and shares the file (never the link). The web keeps the browser's own download.
import { apiBaseUrl } from '@/lib/http';
import { configureAuth, resetAuth } from '@/lib/auth/session';
import { MemoryNativeAuth } from '@/lib/auth/native';
import { createDeviceSharer, type ExportDelivery } from './services';

const LINK = '/api/v1/me/export/0d1e2f30-0000-4000-8000-000000000001/download?token=signed-link';
const SAVED = 'file:///var/mobile/Library/Caches/cm-export/collectionmaxxing-export.zip';

/** Fakes for the phone's file system and share sheet, in the order they were used. */
function fakeDelivery(overrides: Partial<ExportDelivery> = {}) {
  const calls: string[] = [];
  const downloads: { url: string; headers: Record<string, string> }[] = [];
  const shared: string[] = [];
  const removed: string[] = [];
  const delivery: ExportDelivery = {
    download: async (url, headers) => {
      calls.push('download');
      downloads.push({ url, headers });
      return SAVED;
    },
    share: async (fileUrl) => {
      calls.push('share');
      shared.push(fileUrl);
    },
    remove: async (fileUrl) => {
      calls.push('remove');
      removed.push(fileUrl);
    },
    ...overrides,
  };
  return { delivery, calls, downloads, shared, removed };
}

beforeEach(() => configureAuth({ platform: 'native', native: new MemoryNativeAuth('signed-in-token') }));
afterEach(() => resetAuth());

describe('sharing an export on the iPhone', () => {
  it('downloads it with the bearer token and gives the share sheet the saved file, never the link', async () => {
    const { delivery, downloads, shared } = fakeDelivery();

    await createDeviceSharer(delivery).deliver(LINK);

    expect(downloads).toEqual([{ url: `${apiBaseUrl()}${LINK}`, headers: { Authorization: 'Bearer signed-in-token' } }]);
    expect(shared).toEqual([SAVED]);
    expect(shared[0]).toMatch(/^file:\/\//);
  });

  it('deletes the saved file once the share sheet has closed', async () => {
    const { delivery, calls, removed } = fakeDelivery();

    await createDeviceSharer(delivery).deliver(LINK);

    expect(calls).toEqual(['download', 'share', 'remove']);
    expect(removed).toEqual([SAVED]);
  });

  it('deletes the saved file even when the share sheet fails', async () => {
    const { delivery, calls, removed } = fakeDelivery({
      share: async () => {
        throw new Error('no share sheet');
      },
    });

    await expect(createDeviceSharer(delivery).deliver(LINK)).rejects.toThrow('no share sheet');

    expect(calls).toEqual(['download', 'remove']);
    expect(removed).toEqual([SAVED]);
  });

  it('shares nothing when the download fails', async () => {
    const { delivery, calls, shared } = fakeDelivery({
      download: async () => {
        throw new Error('refused');
      },
    });

    await expect(createDeviceSharer(delivery).deliver(LINK)).rejects.toThrow('refused');

    expect(calls).toEqual([]);
    expect(shared).toEqual([]);
  });

  it('is not held up by a file that cannot be deleted', async () => {
    const { delivery } = fakeDelivery({
      remove: async () => {
        throw new Error('busy');
      },
    });

    await expect(createDeviceSharer(delivery).deliver(LINK)).resolves.toBeUndefined();
  });
});

describe('downloading an export on the web', () => {
  it('lets the browser download the link itself and never touches the phone pieces', async () => {
    const { delivery, calls } = fakeDelivery();
    const anchor = { href: '', download: '', click: jest.fn(), remove: jest.fn() };
    Object.defineProperty(globalThis, 'document', {
      value: { createElement: () => anchor, body: { appendChild: jest.fn() } },
      configurable: true,
      writable: true,
    });
    try {
      await createDeviceSharer(delivery).deliver(LINK);
    } finally {
      delete (globalThis as { document?: unknown }).document;
    }

    expect(anchor.href).toBe(`${apiBaseUrl()}${LINK}`);
    expect(anchor.download).toBe('collectionmaxxing-export.zip');
    expect(anchor.click).toHaveBeenCalledTimes(1);
    expect(calls).toEqual([]);
  });
});
