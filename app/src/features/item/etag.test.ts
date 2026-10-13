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
  it('turns the form back into a merge patch, with blanks as null', () => {
    const form = { ...formFromItem(item), notes: '  ', acquiredOn: '' };
    const patch = buildPatch(form, item, '2026-10-13');
    expect(patch.notes).toBeNull();
    expect(patch.acquiredOn).toBeNull();
    expect(patch.condition).toBe('good');
    expect(patch.locationId).toBe('loc');
  });

  it('records the lent date when an item becomes lent', () => {
    const form = { ...formFromItem(item), status: 'lent' as const, lentTo: 'Sam' };
    const patch = buildPatch(form, item, '2026-10-13');
    expect(patch.lentTo).toBe('Sam');
    expect(patch.lentOn).toBe('2026-10-13');
  });

  it('clears the lent details when the item is no longer lent', () => {
    const lent = { ...item, status: 'lent', lentTo: 'Sam', lentOn: '2026-10-01' } as ItemDto;
    const patch = buildPatch({ ...formFromItem(lent), status: 'owned' }, lent, '2026-10-13');
    expect(patch.lentTo).toBeNull();
    expect(patch.lentOn).toBeNull();
  });
});
