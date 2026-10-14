import { BULK_LIMIT, capForBulk, resultMessage } from './bulk';

const make = (n: number) => Array.from({ length: n }, (_, i) => `id-${i}`);

describe('bulk limits', () => {
  it('sends every id when there are 500 or fewer', () => {
    const result = capForBulk(make(500));
    expect(result.ids).toHaveLength(BULK_LIMIT);
    expect(result.message).toBeUndefined();
  });

  it('keeps the first 500 and says so when there are more', () => {
    const result = capForBulk(make(612));
    expect(result.ids).toEqual(make(500));
    expect(result.message).toBe('612 selected. Bulk actions change 500 at a time, so they apply to the first 500.');
  });
});

describe('bulk result copy', () => {
  it('confirms a full success with the count', () => {
    expect(resultMessage('Moved', 3, 3)).toEqual({ ok: true, text: 'Moved 3 items.' });
    expect(resultMessage('Moved', 1, 1)).toEqual({ ok: true, text: 'Moved 1 item.' });
  });

  it('explains a partial failure in plain words and says what to do', () => {
    expect(resultMessage('Moved', 5, 3)).toEqual({
      ok: false,
      text: "Moved 3 of 5 items. The other 2 were deleted or changed by someone else. Reload the list to see what's there now.",
    });
  });

  it('says nothing changed when no item was affected', () => {
    expect(resultMessage('Deleted', 2, 0).text).toBe(
      "Nothing changed. These items were deleted or changed by someone else. Reload the list to see what's there now.",
    );
  });
});
