import { createSilentSnapshot, fitSnapshotSize, SNAPSHOT_LONG_EDGE, type SilentSnapshotDeps } from './silentSnapshot';

const setup = (over: Partial<SilentSnapshotDeps> = {}) => {
  let t = 10_000;
  const removed: string[] = [];
  const made: string[] = [];
  const deps: SilentSnapshotDeps = {
    now: () => t,
    minIntervalMs: 700,
    isCapturing: () => false,
    take: async () => {
      made.push('file:///tmp/full.jpg');
      return { uri: 'file:///tmp/full.jpg', width: 4032, height: 3024, orientation: 'up' };
    },
    shrink: async (_uri, size) => {
      made.push('file:///tmp/small.jpg');
      return { uri: 'file:///tmp/small.jpg', ...size };
    },
    remove: async (uri) => void removed.push(uri),
    ...over,
  };
  return { snap: createSilentSnapshot(deps), removed, made, advance: (ms: number) => (t += ms) };
};

describe('silent snapshot', () => {
  it('returns a small still and deletes the full-size one at once', async () => {
    const { snap, removed } = setup();
    const shot = await snap.take();
    expect(shot).toBe('file:///tmp/small.jpg');
    expect(removed).toEqual(['file:///tmp/full.jpg']);
  });

  it('deletes the small still when it has been read', async () => {
    const { snap, removed } = setup();
    const shot = await snap.take();
    await snap.release(shot!);
    expect(removed).toEqual(['file:///tmp/full.jpg', 'file:///tmp/small.jpg']);
  });

  it('is throttled to the configured rate', async () => {
    const { snap, advance, made } = setup();
    expect(await snap.take()).not.toBeNull();
    advance(300);
    expect(await snap.take()).toBeNull();
    advance(400);
    expect(await snap.take()).not.toBeNull();
    expect(made.length).toBe(4);
  });

  it('never runs while a shutter capture is in progress', async () => {
    let capturing = true;
    const { snap, made } = setup({ isCapturing: () => capturing });
    expect(await snap.take()).toBeNull();
    expect(made).toEqual([]);
    capturing = false;
    expect(await snap.take()).not.toBeNull();
  });

  it('skips a request while another is still running', async () => {
    let open!: () => void;
    const gate = new Promise<void>((r) => (open = r));
    const { snap, advance } = setup({
      take: async () => {
        await gate;
        return { uri: 'file:///tmp/full.jpg', width: 100, height: 100, orientation: 'up' };
      },
    });
    const first = snap.take();
    advance(5000);
    expect(await snap.take()).toBeNull();
    open();
    await first;
  });

  it('cleans up and says nothing when the resize fails', async () => {
    const { snap, removed } = setup({
      shrink: async () => {
        throw new Error('no');
      },
    });
    expect(await snap.take()).toBeNull();
    expect(removed).toEqual(['file:///tmp/full.jpg']);
  });

  it('refuses to delete anything outside the temp folder', async () => {
    const { snap, removed } = setup({ isTemp: (u: string) => u.startsWith('file:///tmp/') });
    await snap.release('file:///Documents/photo.jpg');
    expect(removed).toEqual([]);
  });

  it('never keeps a still that landed outside the temp folder', async () => {
    const { snap, removed } = setup({
      isTemp: (u: string) => u.startsWith('file:///tmp/'),
      take: async () => ({ uri: 'file:///Documents/x.jpg', width: 10, height: 10, orientation: 'up' as const }),
    });
    expect(await snap.take()).toBeNull();
    expect(removed).toEqual([]);
  });
});

describe('snapshot size', () => {
  it('scales the long edge to 640 and keeps the aspect', () => {
    expect(fitSnapshotSize(4032, 3024, 'up')).toEqual({ width: SNAPSHOT_LONG_EDGE, height: 480 });
  });
  it('swaps the sides when the file is stored sideways', () => {
    expect(fitSnapshotSize(4032, 3024, 'right')).toEqual({ width: 480, height: SNAPSHOT_LONG_EDGE });
  });
  it('leaves a small picture alone', () => {
    expect(fitSnapshotSize(300, 200, 'up')).toEqual({ width: 300, height: 200 });
  });
});
