import { ApiError } from '@/lib/http';
import type { GoldenPhotoApi } from './api';
import { AddPhotosController, MAX_PHOTO_BYTES, nameProblem, suggestName } from './addPhotos';
import type { OriginalPickResult, PickedOriginal } from './originalPicker';

// Anything that reached for the image manipulator would be shrinking the original; the whole flow must never touch it.
jest.mock('expo-image-manipulator', () => {
  throw new Error('Adding eval photos must never run a picture through expo-image-manipulator.');
});

const picked = (n: number): PickedOriginal => ({
  uri: `file:///cache/ImagePicker/IMG_${n}.HEIC`,
  width: 4032,
  height: 3024,
  fileName: `IMG_${n}.HEIC`,
  mimeType: 'image/heic',
  fileSize: 3_000_000 + n,
});

const blobOf = (size: number) => new Blob([new Uint8Array(size)]);

function setup(options: { result?: OriginalPickResult; listed?: string[]; sizes?: Record<string, number>; add?: GoldenPhotoApi['add'] } = {}) {
  const sent: { set: string; id: string; size: number }[] = [];
  const reads: string[] = [];
  const api: GoldenPhotoApi = {
    list: async () => (options.listed ?? []).map((id) => ({ id, set: 'single', hasTruth: true, bytes: 1 })),
    add:
      options.add ??
      (async (set, id, photo) => {
        sent.push({ set, id, size: photo.size });
        return { id, set, hasTruth: false, bytes: photo.size };
      }),
  };
  const picker = { pick: jest.fn(async () => options.result ?? { status: 'picked' as const, photos: [picked(1), picked(2), picked(3)] }) };
  const controller = new AddPhotosController({
    picker,
    api,
    read: async (uri) => {
      reads.push(uri);
      return blobOf(options.sizes?.[uri] ?? 1000);
    },
  });
  return { controller, picker, sent, reads, api };
}

const names = (c: AddPhotosController) => c.getState().photos.map((p) => p.name);

describe('suggested names', () => {
  it('counts up from 01 and skips the names the PC already has, in either set', () => {
    expect(suggestName('single', new Set())).toBe('cover-01');
    expect(suggestName('shelf', new Set())).toBe('shelf-01');
    expect(suggestName('single', new Set(['cover-01', 'cover-02', 'shelf-01']))).toBe('cover-03');
    expect(suggestName('shelf', new Set(['cover-01', 'shelf-01']))).toBe('shelf-02');
  });

  it('refuses a name the PC cannot hold, an empty one and one in use (in any letter case)', () => {
    const taken = new Set(['cover-01']);
    expect(nameProblem('cover-02', taken)).toBeNull();
    expect(nameProblem('Back_Shelf-3', taken)).toBeNull();
    expect(nameProblem('', taken)).toBe('Give it a name.');
    expect(nameProblem('has space', taken)).toMatch(/letters, numbers/);
    expect(nameProblem('a/b', taken)).toMatch(/letters, numbers/);
    expect(nameProblem('x'.repeat(65), taken)).toMatch(/letters, numbers/);
    expect(nameProblem('NUL', taken)).toMatch(/Windows keeps that name/);
    expect(nameProblem('Cover-01', taken)).toMatch(/already used/);
  });
});

describe('picking photos', () => {
  it('gives each picked photo a suggested cover name that is free on the PC and unique in the list', async () => {
    const { controller } = setup({ listed: ['cover-01', 'shelf-01', 'cover-02'] });

    await controller.pick();

    expect(controller.getState().phase).toBe('editing');
    expect(names(controller)).toEqual(['cover-03', 'cover-04', 'cover-05']);
    expect(controller.getState().photos.every((p) => p.set === 'single' && p.status === 'ready')).toBe(true);
  });

  it('renames a suggested name when the set changes, and keeps a name the owner typed', async () => {
    const { controller } = setup({ listed: ['cover-01'] });
    await controller.pick();
    const [first, second, third] = controller.getState().photos;

    controller.setSet(first.key, 'shelf');
    controller.rename(second.key, 'my-own-name');
    controller.setSet(second.key, 'shelf');
    controller.setSet(third.key, 'shelf');

    expect(names(controller)).toEqual(['shelf-01', 'my-own-name', 'shelf-02']);
    expect(controller.getState().photos.map((p) => p.set)).toEqual(['shelf', 'shelf', 'shelf']);
  });

  it('names the picks after the photos already on the list when more are picked', async () => {
    const { controller, picker } = setup();
    await controller.pick();
    picker.pick.mockResolvedValueOnce({ status: 'picked', photos: [picked(4)] });

    await controller.pick();

    expect(names(controller)).toEqual(['cover-01', 'cover-02', 'cover-03', 'cover-04']);
  });

  it('still suggests names when the PC cannot be asked for its list', async () => {
    const { controller, api } = setup();
    jest.spyOn(api, 'list').mockRejectedValue(new Error('offline'));

    await controller.pick();

    expect(names(controller)).toEqual(['cover-01', 'cover-02', 'cover-03']);
  });

  it('says so when photo access is off, and when the picker will not open, and does nothing when the owner cancels', async () => {
    const denied = setup({ result: { status: 'denied' } });
    await denied.controller.pick();
    expect(denied.controller.getState().notice).toMatch(/Photos access is off/);
    expect(denied.controller.getState().photos).toEqual([]);

    const broken = setup();
    broken.picker.pick.mockRejectedValue(new Error('no'));
    await broken.controller.pick();
    expect(broken.controller.getState().notice).toBe("Couldn't open your photos. Try again.");

    const cancelled = setup({ result: { status: 'cancelled' } });
    await cancelled.controller.pick();
    expect(cancelled.controller.getState()).toMatchObject({ phase: 'idle', notice: null, photos: [] });
  });

  it('blocks sending while a name cannot be used, and says why for that photo only', async () => {
    const { controller } = setup({ listed: ['back-cover'] });
    await controller.pick();
    const [first, second] = controller.getState().photos;

    controller.rename(first.key, 'has space');
    controller.rename(second.key, 'Back-Cover');

    expect(controller.canSend()).toBe(false);
    expect(controller.nameProblems()).toEqual({
      [first.key]: expect.stringMatching(/letters, numbers/),
      [second.key]: expect.stringMatching(/already used/),
      [controller.getState().photos[2].key]: null,
    });
    await controller.send();
    expect(controller.getState().photos.every((p) => p.status === 'ready')).toBe(true);
  });
});

describe('sending photos', () => {
  it('sends each photo once, one after another, with its set and name, reading its own file', async () => {
    const { controller, sent, reads } = setup({ sizes: { 'file:///cache/ImagePicker/IMG_2.HEIC': 2222 } });
    await controller.pick();
    const [, second, third] = controller.getState().photos;
    controller.setSet(second.key, 'shelf');
    controller.rename(third.key, 'back-cover');
    const positions: number[] = [];
    controller.subscribe(() => {
      const { position, phase } = controller.getState();
      if (phase === 'sending' && positions.at(-1) !== position) positions.push(position);
    });

    await controller.send();

    expect(sent).toEqual([
      { set: 'single', id: 'cover-01', size: 1000 },
      { set: 'shelf', id: 'shelf-01', size: 2222 },
      { set: 'single', id: 'back-cover', size: 1000 },
    ]);
    expect(reads).toEqual(['file:///cache/ImagePicker/IMG_1.HEIC', 'file:///cache/ImagePicker/IMG_2.HEIC', 'file:///cache/ImagePicker/IMG_3.HEIC']);
    expect(positions).toEqual([0, 1, 2, 3]);
    expect(controller.getState().phase).toBe('editing');
    expect(controller.getState().photos.map((p) => p.status)).toEqual(['added', 'added', 'added']);
  });

  it('never has two photos on the way at once', async () => {
    let active = 0;
    let most = 0;
    const { controller } = setup({
      add: async (set, id, photo) => {
        active += 1;
        most = Math.max(most, active);
        await new Promise((resolve) => setTimeout(resolve, 5));
        active -= 1;
        return { id, set, hasTruth: false, bytes: photo.size };
      },
    });
    await controller.pick();

    await controller.send();

    expect(most).toBe(1);
  });

  it('offers a new name when the PC says the name is taken, and sends only that photo on the next try', async () => {
    const { controller, sent } = setup({
      add: async (set, id, photo) => {
        if (id === 'cover-02') throw new ApiError(409, { code: 'evals.id_taken' });
        sent.push({ set, id, size: photo.size });
        return { id, set, hasTruth: false, bytes: photo.size };
      },
    });
    await controller.pick();

    await controller.send();

    const [ok, taken, ok2] = controller.getState().photos;
    expect([ok.status, taken.status, ok2.status]).toEqual(['added', 'taken', 'added']);
    expect(taken.name).toBe('cover-04');
    expect(taken.message).toBe('cover-02 is taken. Try cover-04, or type another name.');
    expect(controller.canSend()).toBe(true);

    // The owner takes the new name: only this photo goes, and the ones already added are not sent again.
    sent.length = 0;
    await controller.send();
    expect(sent).toEqual([{ set: 'single', id: 'cover-04', size: 1000 }]);
    expect(controller.getState().photos.map((p) => p.status)).toEqual(['added', 'added', 'added']);
  });

  it('lets the owner type another name for a photo whose name was taken', async () => {
    const { controller, sent } = setup({
      result: { status: 'picked', photos: [picked(1)] },
      add: async (set, id, photo) => {
        if (id === 'cover-01') throw new ApiError(409, { code: 'evals.id_taken' });
        sent.push({ set, id, size: photo.size });
        return { id, set, hasTruth: false, bytes: photo.size };
      },
    });
    await controller.pick();
    await controller.send();
    const [row] = controller.getState().photos;
    expect(row.status).toBe('taken');

    controller.rename(row.key, 'my-cover');

    expect(controller.getState().photos[0]).toMatchObject({ name: 'my-cover', status: 'ready', message: null });
    await controller.send();
    expect(sent).toEqual([{ set: 'single', id: 'my-cover', size: 1000 }]);
  });

  it('marks a photo too large when the PC says 413, and does not try it again', async () => {
    const { controller } = setup({
      result: { status: 'picked', photos: [picked(1), picked(2)] },
      add: async (set, id, photo) => {
        if (id === 'cover-01') throw new ApiError(413, { code: 'evals.photo_too_large' });
        return { id, set, hasTruth: false, bytes: photo.size };
      },
    });
    await controller.pick();

    await controller.send();

    const [big, fine] = controller.getState().photos;
    expect(big).toMatchObject({ status: 'too_large', message: 'Too large. The limit is 40 MB.' });
    expect(fine.status).toBe('added');
    expect(controller.canSend()).toBe(false);
  });

  it('does not send a photo over the limit at all', async () => {
    const { controller, sent } = setup({
      result: { status: 'picked', photos: [picked(1)] },
      sizes: { 'file:///cache/ImagePicker/IMG_1.HEIC': MAX_PHOTO_BYTES + 1 },
    });
    await controller.pick();

    await controller.send();

    expect(sent).toEqual([]);
    expect(controller.getState().photos[0].status).toBe('too_large');
  });

  it('says a file is not a JPEG or HEIC when the PC says 415', async () => {
    const { controller } = setup({
      result: { status: 'picked', photos: [picked(1)] },
      add: async () => {
        throw new ApiError(415, { code: 'evals.unsupported_picture' });
      },
    });
    await controller.pick();

    await controller.send();

    expect(controller.getState().photos[0]).toMatchObject({ status: 'unsupported', message: "That isn't a JPEG or HEIC photo." });
  });

  it('keeps a photo that could not be sent and sends it again on the next try', async () => {
    let fail = true;
    const { controller, sent } = setup({
      result: { status: 'picked', photos: [picked(1)] },
      add: async (set, id, photo) => {
        if (fail) throw new TypeError('Network request failed');
        sent.push({ set, id, size: photo.size });
        return { id, set, hasTruth: false, bytes: photo.size };
      },
    });
    await controller.pick();

    await controller.send();
    expect(controller.getState().photos[0]).toMatchObject({ status: 'failed', message: "Couldn't reach your server. Check your connection and try again." });
    expect(controller.canSend()).toBe(true);

    fail = false;
    await controller.send();
    expect(sent).toEqual([{ set: 'single', id: 'cover-01', size: 1000 }]);
    expect(controller.getState().photos[0].status).toBe('added');
  });

  it('tells the owner when the PC is not taking eval photos (evals off, or not the owner)', async () => {
    const { controller } = setup({
      result: { status: 'picked', photos: [picked(1)] },
      add: async () => {
        throw new ApiError(404, {});
      },
    });
    await controller.pick();

    await controller.send();

    expect(controller.getState().photos[0]).toMatchObject({ status: 'failed', message: expect.stringMatching(/isn't taking eval photos/) });
  });

  it('drops the photos that went up when more are picked, and keeps the ones still waiting', async () => {
    const { controller, picker } = setup({
      add: async (set, id, photo) => {
        if (id === 'cover-02') throw new ApiError(500, {});
        return { id, set, hasTruth: false, bytes: photo.size };
      },
    });
    await controller.pick();
    await controller.send();
    picker.pick.mockResolvedValueOnce({ status: 'picked', photos: [picked(9)] });

    await controller.pick();

    expect(controller.getState().photos.map((p) => [p.name, p.status])).toEqual([
      ['cover-02', 'failed'],
      ['cover-04', 'ready'],
    ]);
  });

  it('removes a photo from the list', async () => {
    const { controller } = setup();
    await controller.pick();

    controller.remove(controller.getState().photos[1].key);

    expect(names(controller)).toEqual(['cover-01', 'cover-03']);
  });
});
