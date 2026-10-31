import type { EvalPhotoResult } from '@/api/generated/models';
import { MemoryKeyValueStore } from '@/features/scan/tray/trayStore';
import { EvalOutbox } from './outbox';

const result = (photoId: string): EvalPhotoResult => ({ photoId, engine: 'apple_model', outcome: 'ok', items: [], latencyMs: 10, thermalState: 'nominal' });

describe('the eval outbox', () => {
  it('gives back the readings it was keeping for a run, in the order they were read', () => {
    const outbox = new EvalOutbox(new MemoryKeyValueStore());
    outbox.save('run-1', [
      { photoId: 'a', results: [result('a')] },
      { photoId: 'b', results: [result('b')] },
    ]);

    expect(outbox.load('run-1').map((p) => p.photoId)).toEqual(['a', 'b']);
  });

  it('survives the app being killed: a new outbox over the same storage finds what the old one kept', () => {
    const storage = new MemoryKeyValueStore();
    new EvalOutbox(storage).save('run-1', [{ photoId: 'a', results: [result('a')] }]);

    expect(new EvalOutbox(storage).load('run-1')).toEqual([{ photoId: 'a', results: [result('a')] }]);
  });

  it('never gives one run\'s readings to another run', () => {
    const outbox = new EvalOutbox(new MemoryKeyValueStore());
    outbox.save('run-1', [{ photoId: 'a', results: [result('a')] }]);

    expect(outbox.load('run-2')).toEqual([]);
  });

  it('keeps nothing once there is nothing to keep', () => {
    const storage = new MemoryKeyValueStore();
    const outbox = new EvalOutbox(storage);
    outbox.save('run-1', [{ photoId: 'a', results: [result('a')] }]);

    outbox.save('run-1', []);

    expect(storage.getString('eval-outbox-v1')).toBeUndefined();
    expect(outbox.load('run-1')).toEqual([]);
  });

  it('clears everything it kept', () => {
    const storage = new MemoryKeyValueStore();
    const outbox = new EvalOutbox(storage);
    outbox.save('run-1', [{ photoId: 'a', results: [result('a')] }]);

    outbox.clear();

    expect(outbox.load('run-1')).toEqual([]);
  });

  it('reads damaged storage as empty rather than failing the run', () => {
    const storage = new MemoryKeyValueStore();
    storage.set('eval-outbox-v1', '{ not json');
    expect(new EvalOutbox(storage).load('run-1')).toEqual([]);

    storage.set('eval-outbox-v1', JSON.stringify({ runId: 'run-1', photos: [{ photoId: 'a' }, 'nope', { photoId: 'b', results: [] }] }));
    expect(new EvalOutbox(storage).load('run-1').map((p) => p.photoId)).toEqual(['b']);
  });

  it('keeps going without storage that fails, and says nothing', () => {
    const broken = {
      getString: () => {
        throw new Error('storage is gone');
      },
      set: () => {
        throw new Error('storage is gone');
      },
      remove: () => {
        throw new Error('storage is gone');
      },
    };
    const outbox = new EvalOutbox(broken);

    expect(() => outbox.save('run-1', [{ photoId: 'a', results: [] }])).not.toThrow();
    expect(outbox.load('run-1')).toEqual([]);
    expect(() => outbox.clear()).not.toThrow();
  });
});
