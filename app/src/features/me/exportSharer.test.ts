// How a finished export reaches the person. The download needs the sign-in, and the iPhone's is a bearer token a share target cannot
// send, so the phone fetches the zip itself and shares the file (never the link). The web keeps the browser's own download.
import { apiBaseUrl } from '@/lib/http';
import { configureAuth, resetAuth } from '@/lib/auth/session';
import { MemoryNativeAuth } from '@/lib/auth/native';
import { createDeviceSharer, isZipSignature, requireZipFile, type ExportDelivery } from './services';

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

  it('shares nothing when what came back is not the export, and says so', async () => {
    // An expired sign-in or a server error can still be saved as the .zip: the delivery refuses it, so the sheet never opens on it.
    const { delivery, shared, removed } = fakeDelivery({
      download: async () => {
        throw new Error("That download wasn't the export.");
      },
    });

    await expect(createDeviceSharer(delivery).deliver(LINK)).rejects.toThrow("That download wasn't the export.");

    expect(shared).toEqual([]);
    expect(removed).toEqual([]); // the delivery already deleted what it saved
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

const bytes = (...values: number[]) => Uint8Array.from(values);
const ZIP_HEAD = bytes(0x50, 0x4b, 0x03, 0x04);
const textBytes = (value: string) => Uint8Array.from(Buffer.from(value, 'utf8'));

describe('telling a saved export from an error saved in its place', () => {
  it('knows a zip by its first four bytes', () => {
    expect(isZipSignature(ZIP_HEAD)).toBe(true);
    expect(isZipSignature(bytes(0x50, 0x4b, 0x03, 0x04, 0x14, 0x00))).toBe(true);
  });

  it('does not take an error body, an empty file or a short read for a zip', () => {
    expect(isZipSignature(textBytes('{"title":"Unauthorized","status":401}'))).toBe(false);
    expect(isZipSignature(textBytes('<html>Bad gateway</html>'))).toBe(false);
    expect(isZipSignature(bytes())).toBe(false);
    expect(isZipSignature(bytes(0x50, 0x4b, 0x03))).toBe(false);
    expect(isZipSignature(bytes(0x50, 0x4b, 0x05, 0x06))).toBe(false); // an empty archive: an export always holds files
  });

  it('keeps a file that starts like a zip', async () => {
    const removed: string[] = [];

    await requireZipFile(SAVED, async () => ZIP_HEAD, async (fileUrl) => void removed.push(fileUrl));

    expect(removed).toEqual([]);
  });

  it.each([
    ['an error body', textBytes('{"title":"Your session ended."}')],
    ['an empty file', bytes()],
  ])('deletes %s and refuses it', async (_name, head) => {
    const removed: string[] = [];

    await expect(requireZipFile(SAVED, async () => head, async (fileUrl) => void removed.push(fileUrl))).rejects.toThrow(/wasn't the export/);

    expect(removed).toEqual([SAVED]);
  });

  it('deletes the file and refuses it when it cannot be read', async () => {
    const removed: string[] = [];
    const unreadable = async () => {
      throw new Error('no such file');
    };

    await expect(requireZipFile(SAVED, unreadable, async (fileUrl) => void removed.push(fileUrl))).rejects.toThrow('no such file');

    expect(removed).toEqual([SAVED]);
  });
});
