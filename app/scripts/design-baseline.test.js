// The design ratchet only fails when a file+rule count grows past the baseline (#307).
const { buildEntries, compareToBaseline, summarize } = require('./design-baseline');

const message = (ruleId, line) => ({ ruleId, line, message: `${ruleId} at ${line}` });
const root = '/repo/app';

function resultsFor(entries) {
  const byFile = new Map();
  for (const [file, rule, line] of entries) {
    const path = `${root}/${file}`;
    byFile.set(path, [...(byFile.get(path) || []), message(rule, line)]);
  }
  return [...byFile].map(([filePath, messages]) => ({ filePath, messages }));
}

describe('design baseline ratchet', () => {
  it('passes a clean tree against an empty baseline', () => {
    const { failures } = compareToBaseline(summarize([], root), { entries: [] });
    expect(failures).toEqual([]);
  });

  it('fails a new violation in a file with no baseline entry', () => {
    const summary = summarize(resultsFor([['src/ui/New.tsx', 'cm-design/no-raw-colors', 3]]), root);
    const { failures } = compareToBaseline(summary, { entries: [] });
    expect(failures).toHaveLength(1);
    expect(failures[0]).toMatchObject({ file: 'src/ui/New.tsx', rule: 'cm-design/no-raw-colors', count: 1, allowed: 0 });
  });

  it('passes counts at or below the baseline, and fails counts above it', () => {
    const summary = summarize(
      resultsFor([
        ['src/a.tsx', 'cm-design/no-inline-styles', 1],
        ['src/a.tsx', 'cm-design/no-inline-styles', 2],
        ['src/b.tsx', 'cm-design/no-inline-styles', 1],
        ['src/b.tsx', 'cm-design/no-inline-styles', 2],
        ['src/b.tsx', 'cm-design/no-inline-styles', 3],
      ]),
      root,
    );
    const baseline = {
      entries: [
        { file: 'src/a.tsx', rule: 'cm-design/no-inline-styles', count: 2 },
        { file: 'src/b.tsx', rule: 'cm-design/no-inline-styles', count: 2 },
      ],
    };
    const { failures } = compareToBaseline(summary, baseline);
    expect(failures).toHaveLength(1);
    expect(failures[0]).toMatchObject({ file: 'src/b.tsx', count: 3, allowed: 2 });
  });

  it('reports a new rule in a baselined file while other rules stay grandfathered', () => {
    const summary = summarize(
      resultsFor([
        ['src/a.tsx', 'cm-design/no-inline-styles', 1],
        ['src/a.tsx', 'cm-design/no-raw-colors', 9],
      ]),
      root,
    );
    const baseline = { entries: [{ file: 'src/a.tsx', rule: 'cm-design/no-inline-styles', count: 1 }] };
    const { failures } = compareToBaseline(summary, baseline);
    expect(failures).toHaveLength(1);
    expect(failures[0].rule).toBe('cm-design/no-raw-colors');
  });

  it('names paid-off debt without failing', () => {
    const { failures, paidOff } = compareToBaseline(summarize([], root), {
      entries: [{ file: 'src/a.tsx', rule: 'cm-design/no-inline-styles', count: 4 }],
    });
    expect(failures).toEqual([]);
    expect(paidOff).toEqual([{ file: 'src/a.tsx', rule: 'cm-design/no-inline-styles' }]);
  });

  it('rebuilds entries sorted, keeping reasons and dropping paid-off debt', () => {
    const summary = summarize(resultsFor([['src/b.tsx', 'cm-design/no-raw-text', 1]]), root);
    const entries = buildEntries(summary, [{ file: 'src/b.tsx', rule: 'cm-design/no-raw-text', count: 5, reason: 'kept' }], () => 'default');
    expect(entries).toEqual([{ file: 'src/b.tsx', rule: 'cm-design/no-raw-text', count: 1, reason: 'kept' }]);
  });
});
