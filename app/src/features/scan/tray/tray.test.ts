import { createStabilityFilter } from './stability';
import {
  addCopy,
  emptyTray,
  lineStatus,
  markBatchSent,
  MAX_BATCH,
  planBatches,
  removeLine,
  scanCode,
  totalCopies,
  type TrayState,
} from './tray';

const dune = { value: '9780441172719', symbology: 'ean-13' };
const mario = { value: '045496870010', symbology: 'ean-13' };

const keys = () => {
  let n = 0;
  return () => `key-${++n}`;
};

describe('stability filter', () => {
  it('ignores a code seen in only one frame', () => {
    const filter = createStabilityFilter();
    expect(filter.observe(['A'])).toEqual([]);
    expect(filter.observe([])).toEqual([]);
    expect(filter.observe([])).toEqual([]);
  });

  it('reports a code once it is seen in two of the last four frames', () => {
    const filter = createStabilityFilter();
    expect(filter.observe(['A'])).toEqual([]);
    expect(filter.observe([])).toEqual([]);
    expect(filter.observe(['A'])).toEqual(['A']);
  });

  it('reports a code once while it stays in view', () => {
    const filter = createStabilityFilter();
    filter.observe(['A']);
    expect(filter.observe(['A'])).toEqual(['A']);
    expect(filter.observe(['A'])).toEqual([]);
    expect(filter.observe(['A'])).toEqual([]);
  });

  it('reports it again after it has been out of view for a whole window', () => {
    const filter = createStabilityFilter();
    filter.observe(['A']);
    filter.observe(['A']);
    for (let i = 0; i < 4; i++) filter.observe([]);
    filter.observe(['A']);
    expect(filter.observe(['A'])).toEqual(['A']);
  });

  it('handles several codes in one frame independently', () => {
    const filter = createStabilityFilter();
    filter.observe(['A', 'B']);
    expect(filter.observe(['A', 'B', 'C']).sort()).toEqual(['A', 'B']);
    expect(filter.observe(['C'])).toEqual(['C']);
  });
});

describe('tray de-dupe', () => {
  it('adds a new code as one copy', () => {
    const { state, result } = scanCode(emptyTray(), dune, 1000);
    expect(result).toBe('added');
    expect(state.lines).toHaveLength(1);
    expect(state.lines[0]).toMatchObject({ code: dune.value, copies: 1, sentCopies: 0 });
  });

  it('adds nothing when the same code is seen again inside the cooldown', () => {
    const first = scanCode(emptyTray(), dune, 1000).state;
    const { state, result } = scanCode(first, dune, 2000, 5000);
    expect(result).toBe('ignored');
    expect(totalCopies(state)).toBe(1);
  });

  it('still adds no copy when the same code comes back after the cooldown, but says so', () => {
    const first = scanCode(emptyTray(), dune, 1000).state;
    const { state, result } = scanCode(first, dune, 20_000, 5000);
    expect(result).toBe('duplicate');
    expect(totalCopies(state)).toBe(1);
  });

  it('adds a copy only on an explicit +1', () => {
    const first = scanCode(emptyTray(), dune, 1000).state;
    expect(totalCopies(addCopy(first, dune.value))).toBe(2);
  });

  it('keeps the rest of the tray when one line is removed', () => {
    let state = scanCode(emptyTray(), dune, 1000).state;
    state = scanCode(state, mario, 1100).state;
    expect(removeLine(state, dune.value).lines.map((l) => l.code)).toEqual([mario.value]);
  });
});

describe('tray batches', () => {
  const trayOf = (n: number): TrayState => {
    let state = emptyTray();
    for (let i = 0; i < n; i++) state = scanCode(state, { value: `code-${i}`, symbology: 'ean-13' }, 1000 + i).state;
    return state;
  };

  it('sends at most 50 codes per batch, each with its own key', () => {
    const planned = planBatches(trayOf(120), keys());
    expect(planned.outbox.map((b) => b.entries.length)).toEqual([MAX_BATCH, MAX_BATCH, 20]);
    expect(new Set(planned.outbox.map((b) => b.key)).size).toBe(3);
  });

  it('does not plan the same copies twice, so a retry reuses the same batch and key', () => {
    const once = planBatches(trayOf(3), keys());
    const twice = planBatches(once, keys());
    expect(twice.outbox).toEqual(once.outbox);
  });

  it('plans only the extra copy after a +1', () => {
    const planned = planBatches(trayOf(1), keys());
    const withCopy = planBatches(addCopy(planned, 'code-0'), keys());
    expect(withCopy.outbox.map((b) => b.entries)).toEqual([
      [{ value: 'code-0', symbology: 'ean-13', copies: 1 }],
      [{ value: 'code-0', symbology: 'ean-13', copies: 1 }],
    ]);
  });

  it('counts a batch as sent once, across batches', () => {
    let state = planBatches(trayOf(2), keys());
    state = markBatchSent(state, 'key-1');
    expect(state.outbox).toEqual([]);
    expect(state.lines.map((l) => l.sentCopies)).toEqual([1, 1]);
    expect(markBatchSent(state, 'key-1')).toBe(state);
  });

  it('drops removed codes from waiting batches', () => {
    const state = removeLine(planBatches(trayOf(2), keys()), 'code-0');
    expect(state.outbox[0]?.entries.map((e) => e.value)).toEqual(['code-1']);
  });

  it('shows QUEUED offline and a lookup while online', () => {
    const line = trayOf(1).lines[0]!;
    expect(lineStatus(line, false)).toBe('queued');
    expect(lineStatus(line, true)).toBe('sending');
    expect(lineStatus({ ...line, sentCopies: 1 }, false)).toBe('sent');
  });
});
