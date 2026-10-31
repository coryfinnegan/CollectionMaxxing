import { buildPatch, formFromItem, ifMatchHeader, isValidDate, newIdempotencyKey } from './etag';
import type { ItemDto } from '@/api/generated/models';

const item = {
  id: 'i1',
  status: 'owned',
  condition: 'good',
  completeness: null,
  locationId: 'loc',
  notes: 'Slipcover missing',
  acquiredOn: '2024-06-02',
  lentTo: null,
  lentOn: null,
} as ItemDto;

describe('ifMatchHeader', () => {
  it('sends the ETag exactly as the server gave it', () => {
    expect(ifMatchHeader('"v3"')).toEqual({ 'If-Match': '"v3"' });
  });

  it('quotes a bare value', () => {
    expect(ifMatchHeader('v3')).toEqual({ 'If-Match': '"v3"' });
  });
});

describe('newIdempotencyKey', () => {
  it('is different every time', () => {
    expect(newIdempotencyKey()).not.toBe(newIdempotencyKey());
  });
});

describe('isValidDate', () => {
  it('accepts empty and real YYYY-MM-DD dates only', () => {
    expect(isValidDate('')).toBe(true);
    expect(isValidDate('2024-06-02')).toBe(true);
    expect(isValidDate('2024-13-02')).toBe(false);
    expect(isValidDate('June 2')).toBe(false);
  });
});

describe('buildPatch', () => {
  it('sends nothing for a form nobody touched', () => {
    expect(buildPatch(formFromItem(item), item, '2026-10-13')).toEqual({});
  });

  it('sends only the fields that differ from the version the form opened with', () => {
    const form = { ...formFromItem(item), condition: 'mint' as const };
    expect(buildPatch(form, item, '2026-10-13')).toEqual({ condition: 'mint' });
  });

  it('sends blanked text as null, which clears the field', () => {
    const form = { ...formFromItem(item), notes: '  ', acquiredOn: '' };
    expect(buildPatch(form, item, '2026-10-13')).toEqual({ notes: null, acquiredOn: null });
  });

  it('does not count whitespace around unchanged text as an edit', () => {
    const form = { ...formFromItem(item), notes: '  Slipcover missing ' };
    expect(buildPatch(form, item, '2026-10-13')).toEqual({});
  });

  it('can clear a location or a condition', () => {
    const form = { ...formFromItem(item), locationId: null, condition: null };
    expect(buildPatch(form, item, '2026-10-13')).toEqual({ locationId: null, condition: null });
  });

  it('records the lent date when an item becomes lent', () => {
    const form = { ...formFromItem(item), status: 'lent' as const, lentTo: 'Sam' };
    expect(buildPatch(form, item, '2026-10-13')).toEqual({ status: 'lent', lentTo: 'Sam', lentOn: '2026-10-13' });
  });

  it('clears the lent details when the item is no longer lent', () => {
    const lent = { ...item, status: 'lent', lentTo: 'Sam', lentOn: '2026-10-01' } as ItemDto;
    const patch = buildPatch({ ...formFromItem(lent), status: 'owned' }, lent, '2026-10-13');
    expect(patch).toEqual({ status: 'owned', lentTo: null, lentOn: null });
  });

  it('keeps the original lent date when only the borrower changes', () => {
    const lent = { ...item, status: 'lent', lentTo: 'Sam', lentOn: '2026-10-01' } as ItemDto;
    const patch = buildPatch({ ...formFromItem(lent), lentTo: 'Alex' }, lent, '2026-10-13');
    expect(patch).toEqual({ lentTo: 'Alex' });
  });
});
