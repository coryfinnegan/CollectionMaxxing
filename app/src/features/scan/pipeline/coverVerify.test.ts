// Stage 2 sender (#438): what the phone posts to :verify-cover — the cover still plus the
// detected cover bounds as a quad — and when it stays quiet instead.
import type { CandidateDto } from '@/api/generated/models';
import { candidate } from '@/features/review/testData';
import type { CapturedPhoto } from '../autosnap/snapPhoto';
import { MemoryKeyValueStore } from '../tray/trayStore';
import {
  MAX_VERIFY_BYTES,
  coverCaptureOf,
  quadJsonOf,
  verifyCoverPhoto,
  type CoverCapture,
  type CoverVerifyDeps,
} from './coverVerify';
import type { PipelineApi } from './pipelineApi';
import { MemorySnapFiles, SnapStore } from './snapStore';

const BOX = { x: 0.125, y: 0.25, width: 0.5, height: 0.5 };
const blobOf = (size: number) => ({ size }) as Blob;
const alternativesOf = (ids: string[]) =>
  ids.map((releaseId) => ({ releaseId })) as CandidateDto['alternatives'];

const snapWith = (snapId: string, itemCount: number) => ({ snapId, items: Array.from({ length: itemCount }, (_, i) => ({ localId: `item-${i}` })) });
const itemOf = (localId: string) => ({ localId });

const storedSnap = async (snapId: string, box = BOX) => {
  const snaps = new SnapStore({ files: new MemorySnapFiles(), storage: new MemoryKeyValueStore() });
  await snaps.saveStill(snapId, { uri: 'file:///tmp/still.jpg', width: 2000, height: 1500 });
  await snaps.saveItem(snapId, { localId: 'item-0', text: 'Suddenly You', box });
  return snaps;
};

describe('quadJsonOf', () => {
  it('names the four corners clockwise from the top left', () => {
    expect(JSON.parse(quadJsonOf(BOX)!)).toEqual({
      topLeft: { x: 0.125, y: 0.25 },
      topRight: { x: 0.625, y: 0.25 },
      bottomRight: { x: 0.625, y: 0.75 },
      bottomLeft: { x: 0.125, y: 0.75 },
    });
  });

  it('pulls slightly-out-of-range corners back into the picture', () => {
    expect(JSON.parse(quadJsonOf({ x: -0.25, y: 0.25, width: 0.75, height: 1 })!)).toEqual({
      topLeft: { x: 0, y: 0.25 },
      topRight: { x: 0.5, y: 0.25 },
      bottomRight: { x: 0.5, y: 1 },
      bottomLeft: { x: 0, y: 1 },
    });
  });

  it.each([
    ['a flat box', { x: 0.125, y: 0.25, width: 0, height: 0.5 }],
    ['an inverted box', { x: 0.125, y: 0.25, width: 0.5, height: -0.1 }],
    ['a sliver', { x: 0.125, y: 0.25, width: 1e-4, height: 1e-4 }],
    ['a box off the picture', { x: 2, y: 2, width: 0.5, height: 0.5 }],
    ['a NaN corner', { x: Number.NaN, y: 0.25, width: 0.5, height: 0.5 }],
    ['an infinite corner', { x: 0.125, y: Number.POSITIVE_INFINITY, width: 0.5, height: 0.5 }],
  ])('sends no quad for %s', (_name, box) => {
    expect(quadJsonOf(box)).toBeUndefined();
  });
});

describe('coverCaptureOf', () => {
  it('pairs the still with the item quad', async () => {
    const snaps = await storedSnap('snap-1');
    const capture = coverCaptureOf(snaps, snapWith('snap-1', 2), itemOf('item-0'));
    expect(capture?.stillUri).toBe('file:///snaps/snap-1-still.jpg');
    expect(capture && { width: capture.width, height: capture.height }).toEqual({ width: 2000, height: 1500 });
    expect(JSON.parse(capture!.quad!)).toEqual(JSON.parse(quadJsonOf(BOX)!));
  });

  it('sends the whole still without a quad when a lone item has no box', async () => {
    const snaps = new SnapStore({ files: new MemorySnapFiles(), storage: new MemoryKeyValueStore() });
    await snaps.saveStill('snap-1', { uri: 'file:///tmp/still.jpg', width: 1200, height: 900 });
    await snaps.saveItem('snap-1', { localId: 'item-0', text: 'Suddenly You' });
    const capture = coverCaptureOf(snaps, snapWith('snap-1', 1), itemOf('item-0'));
    expect(capture).toEqual({ stillUri: 'file:///snaps/snap-1-still.jpg', width: 1200, height: 900, quad: undefined });
  });

  it('stays quiet when the cover cannot be isolated: a boxless item among several, or no still', async () => {
    const snaps = new SnapStore({ files: new MemorySnapFiles(), storage: new MemoryKeyValueStore() });
    await snaps.saveStill('snap-1', { uri: 'file:///tmp/still.jpg', width: 1200, height: 900 });
    await snaps.saveItem('snap-1', { localId: 'item-0', text: 'Suddenly You' });
    expect(coverCaptureOf(snaps, snapWith('snap-1', 2), itemOf('item-0'))).toBeUndefined();
    expect(coverCaptureOf(snaps, snapWith('snap-gone', 1), itemOf('item-0'))).toBeUndefined();
  });
});

describe('verifyCoverPhoto', () => {
  const card = (extra: Partial<CandidateDto> = {}) =>
    candidate({ id: 'candidate-1', resolution: 'ambiguous', needsArt: true, alternatives: alternativesOf(['r1', 'r2']), ...extra });
  const capture = (extra: Partial<CoverCapture> = {}): CoverCapture => ({
    stillUri: 'file:///snaps/snap-1-still.jpg',
    width: 1200,
    height: 900,
    quad: quadJsonOf(BOX),
    ...extra,
  });

  const setup = (verifyCover: PipelineApi['verifyCover']) => {
    const api = { verifyCover } as unknown as PipelineApi;
    const reads: string[] = [];
    const discarded: string[] = [];
    let shrunk = 0;
    const deps: CoverVerifyDeps = {
      readFile: jest.fn(async (uri: string) => {
        reads.push(uri);
        return blobOf(100);
      }),
      shrink: jest.fn(async (photo: CapturedPhoto, target: { width: number; height: number } | null) => ({
        uri: `file:///cache/verify-${shrunk++}.jpg`,
        width: target?.width ?? photo.width,
        height: target?.height ?? photo.height,
      })),
      discard: jest.fn(async (uri: string) => void discarded.push(uri)),
    };
    return { api, deps, reads, discarded };
  };

  it('posts a small still as it is, with the quad', async () => {
    const updated = card();
    const { api, deps, reads } = setup(jest.fn(async () => updated));
    expect(await verifyCoverPhoto(deps, api, 'session-1', card(), capture())).toBe(updated);
    expect(deps.shrink).not.toHaveBeenCalled();
    expect(reads).toEqual(['file:///snaps/snap-1-still.jpg']);
    expect(api.verifyCover).toHaveBeenCalledWith('session-1', 'candidate-1', blobOf(100), quadJsonOf(BOX));
  });

  it('shrinks a big still, sends the copy, and deletes it afterwards', async () => {
    const updated = card();
    const { api, deps, reads, discarded } = setup(jest.fn(async () => updated));
    expect(await verifyCoverPhoto(deps, api, 'session-1', card(), capture({ width: 2000, height: 1500 }))).toBe(updated);
    expect(deps.shrink).toHaveBeenCalledWith(
      { uri: 'file:///snaps/snap-1-still.jpg', width: 2000, height: 1500, orientation: 1 },
      { width: 1600, height: 1200 },
    );
    expect(reads).toEqual(['file:///cache/verify-0.jpg']);
    expect(discarded).toEqual(['file:///cache/verify-0.jpg']);
  });

  it('never deletes the stored still, even when shrinking answers with it', async () => {
    const updated = card();
    const { api, deps, discarded } = setup(jest.fn(async () => updated));
    deps.shrink = jest.fn(async (photo: CapturedPhoto) => ({ uri: photo.uri, width: photo.width, height: photo.height }));
    expect(await verifyCoverPhoto(deps, api, 'session-1', card(), capture({ width: 2000, height: 1500 }))).toBe(updated);
    expect(discarded).toEqual([]);
  });

  it('sends without a quad when the capture has none', async () => {
    const { api, deps } = setup(jest.fn(async () => card()));
    await verifyCoverPhoto(deps, api, 'session-1', card(), capture({ quad: undefined }));
    expect(api.verifyCover).toHaveBeenCalledWith('session-1', 'candidate-1', blobOf(100), undefined);
  });

  it('refuses an oversize capture before anything is sent', async () => {
    const verifyCover = jest.fn(async () => card());
    const { api, deps } = setup(verifyCover);
    deps.readFile = jest.fn(async () => blobOf(MAX_VERIFY_BYTES + 1));
    expect(await verifyCoverPhoto(deps, api, 'session-1', card(), capture())).toBeUndefined();
    expect(verifyCover).not.toHaveBeenCalled();
  });

  it('sends nothing for a card with no alternatives to compare', async () => {
    const verifyCover = jest.fn(async () => card());
    const { api, deps, reads } = setup(verifyCover);
    expect(await verifyCoverPhoto(deps, api, 'session-1', card({ alternatives: null }), capture())).toBeUndefined();
    expect(reads).toEqual([]);
    expect(verifyCover).not.toHaveBeenCalled();
  });

  it.each([
    ['the still cannot be read', { failRead: true, failPost: false }],
    ['the post fails', { failRead: false, failPost: true }],
  ])('leaves the card as it is when %s', async (_name, { failRead, failPost }) => {
    const { api, deps } = setup(failPost ? jest.fn(async () => { throw new Error('offline'); }) : jest.fn(async () => card()));
    if (failRead) deps.readFile = jest.fn(async () => { throw new Error('gone'); });
    expect(await verifyCoverPhoto(deps, api, 'session-1', card(), capture())).toBeUndefined();
  });
});
