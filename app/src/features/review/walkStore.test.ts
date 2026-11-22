// The walk's memory (#833): it survives a restart at the same card, kept apart per scan session.
import { MemoryKeyValueStore } from '@/features/scan/tray/trayStore';
import { answerWalk, joinWalk } from './walk';
import { createWalkStore } from './walkStore';

describe('walk store', () => {
  it('survives a restart at the same card', () => {
    const storage = new MemoryKeyValueStore();
    const first = createWalkStore({ storage });
    first.getState().update('s1', (w) => ({ ...answerWalk(joinWalk(w, ['a', 'b', 'c']), { id: 'a', kind: 'accepted', releaseId: 'r1' }), place: 'b' }));
    const again = createWalkStore({ storage });
    expect(again.getState().walks.s1).toEqual({ order: ['a', 'b', 'c'], stack: [{ id: 'a', kind: 'accepted', releaseId: 'r1' }], place: 'b', ended: false });
  });

  it('keeps each session apart and only the last few', () => {
    const store = createWalkStore({ storage: new MemoryKeyValueStore() });
    for (const id of ['s1', 's2', 's3', 's4', 's5', 's6']) store.getState().update(id, (w) => joinWalk(w, [id]));
    expect(Object.keys(store.getState().walks).sort()).toEqual(['s2', 's3', 's4', 's5', 's6']);
    expect(store.getState().walks.s6?.order).toEqual(['s6']);
    store.getState().forget('s6');
    expect(store.getState().walks.s6).toBeUndefined();
  });

  it("keeps only the open scan's walk once another scan is open", () => {
    const store = createWalkStore({ storage: new MemoryKeyValueStore() });
    store.getState().update('old', (w) => joinWalk(w, ['a']));
    store.getState().update('new', (w) => joinWalk(w, ['b']));
    store.getState().keepOnly('new');
    expect(Object.keys(store.getState().walks)).toEqual(['new']);
    store.getState().keepOnly(null);
    expect(store.getState().walks).toEqual({});
  });

  it('starts fresh from a damaged file', () => {
    const storage = new MemoryKeyValueStore();
    storage.set('review-walk-v1', '{not json');
    expect(createWalkStore({ storage }).getState().walks).toEqual({});
  });
});
