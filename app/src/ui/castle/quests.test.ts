// The quest board's rules (#362): three deadline-free quests derived from the
// shelves, an opt-in pin, and one-shot celebrations only for new completions.
import { freshQuests, questDefs, questsFor, setQuestBoardStorage, setQuestSeenStorage, useQuestBoard, type QuestInput } from './quests';

function memoryStore() {
  const map = new Map<string, string>();
  return {
    getString: (key: string) => map.get(key),
    set: (key: string, value: string) => void map.set(key, value),
  };
}

const empty: QuestInput = { fullestShelf: 0, shelvesUsed: 0, bestSet: 0 };

beforeEach(() => {
  setQuestBoardStorage(memoryStore());
  setQuestSeenStorage(memoryStore());
  useQuestBoard.setState({ pinned: false });
});

describe('questsFor', () => {
  it('starts every quest at zero with a plain progress line', () => {
    const quests = questsFor(empty);

    expect(quests.map((q) => q.id)).toEqual(['full-shelf', 'three-shelves', 'full-series']);
    expect(quests.map((q) => q.line)).toEqual(['0 of 10.', '0 of 3.', '0 of 3.']);
    expect(quests.every((q) => !q.done)).toBe(true);
  });

  it('counts a full shelf at ten items on one shelf', () => {
    expect(questsFor({ ...empty, fullestShelf: 7 })[0]).toMatchObject({ progress: 7, target: 10, done: false, line: '7 of 10.' });
    expect(questsFor({ ...empty, fullestShelf: 10 })[0]).toMatchObject({ done: true, line: 'Done. It stays done.' });
    expect(questsFor({ ...empty, fullestShelf: 28 })[0]).toMatchObject({ done: true });
  });

  it('counts shelves used and copies of one work', () => {
    expect(questsFor({ ...empty, shelvesUsed: 2 })[1]).toMatchObject({ done: false, line: '2 of 3.' });
    expect(questsFor({ ...empty, shelvesUsed: 3 })[1]).toMatchObject({ done: true });
    expect(questsFor({ ...empty, bestSet: 2 })[2]).toMatchObject({ done: false, line: '2 of 3.' });
    expect(questsFor({ ...empty, bestSet: 3 })[2]).toMatchObject({ done: true });
  });

  it('clamps ragged counts at zero instead of going negative', () => {
    expect(questsFor({ fullestShelf: -4, shelvesUsed: -1, bestSet: -2 }).map((q) => q.line)).toEqual(['0 of 10.', '0 of 3.', '0 of 3.']);
  });

  it('carries no deadline, expiry, streak or failure anywhere in the shape', () => {
    const forbidden = ['deadline', 'due', 'expiry', 'expires', 'streak', 'day', 'days', 'fail', 'penalty', 'miss'];
    for (const def of questDefs) {
      for (const key of Object.keys(def)) {
        expect(forbidden).not.toContain(key);
      }
      expect(`${def.title} ${def.goal}`.toLowerCase()).not.toMatch(/deadline|expir|streak|daily|fail|penalty|miss you/);
    }
    for (const quest of questsFor({ fullestShelf: 4, shelvesUsed: 1, bestSet: 1 })) {
      expect(quest.line).not.toMatch(/deadline|expir|streak|daily|fail|penalty|miss/i);
    }
  });
});

describe('useQuestBoard', () => {
  it('starts unpinned until the person pins it', () => {
    expect(useQuestBoard.getState().pinned).toBe(false);
  });

  it('keeps the pin on this device', () => {
    useQuestBoard.getState().set(true);
    expect(useQuestBoard.getState().pinned).toBe(true);

    useQuestBoard.setState({ pinned: false });
    useQuestBoard.getState().reload();
    expect(useQuestBoard.getState().pinned).toBe(true);
  });
});

describe('freshQuests', () => {
  it('stays quiet on the first view, even with finished quests on the shelves', () => {
    const quests = questsFor({ fullestShelf: 28, shelvesUsed: 3, bestSet: 3 });

    expect(freshQuests(quests)).toEqual([]);
  });

  it('names a newly finished quest exactly once', () => {
    freshQuests(questsFor(empty));

    const done = questsFor({ ...empty, fullestShelf: 10 });
    expect(freshQuests(done).map((q) => q.id)).toEqual(['full-shelf']);
    expect(freshQuests(done)).toEqual([]);
  });

  it('survives a ragged store without celebrating everything', () => {
    const store = memoryStore();
    store.set('quest-seen', 'not json');
    setQuestSeenStorage(store);

    expect(freshQuests(questsFor({ fullestShelf: 28, shelvesUsed: 3, bestSet: 3 }))).toEqual([]);
  });
});
