// Verifying an item's edition from its barcode, against the mock API: the edition verifies in place and
// the copy data stays, and a cross-work barcode is refused naming its owner.
import { ApiError } from '@/lib/http';
import { CROSS_WORK_BARCODE, itemsState, resetItems } from '@/mocks/items';
import { submitItemBarcodeUpgrade } from './upgrade';

const MATRIX = 'c0000000-0000-4000-8000-000000000001';
const entry = () => itemsState.entries.find((e) => e.item.id === MATRIX)!;

beforeEach(() => {
  resetItems();
});

describe('submitItemBarcodeUpgrade', () => {
  it('verifies the edition in place and keeps the copy data', async () => {
    entry().item.editionVerified = false;
    entry().item.notes = 'First printing?';

    const upgraded = await submitItemBarcodeUpgrade('library-1', MATRIX, '9780441172719');

    expect(upgraded.editionVerified).toBe(true);
    expect(upgraded.notes).toBe('First printing?');
    expect(entry().item.editionVerified).toBe(true);
    expect(entry().item.notes).toBe('First printing?');
  });

  it('a cross-work barcode is refused naming its owner, and nothing changes', async () => {
    entry().item.editionVerified = false;

    const error: unknown = await submitItemBarcodeUpgrade('library-1', MATRIX, CROSS_WORK_BARCODE).catch((e: unknown) => e);

    expect(error).toBeInstanceOf(ApiError);
    expect((error as ApiError).status).toBe(409);
    expect((error as ApiError).problem.detail).toBe('That barcode belongs to Dune Messiah.');
    expect(entry().item.editionVerified).toBe(false);
  });

  it('an unknown item is a 404', async () => {
    const error: unknown = await submitItemBarcodeUpgrade('library-1', 'missing', '9780441172719').catch((e: unknown) => e);

    expect(error).toBeInstanceOf(ApiError);
    expect((error as ApiError).status).toBe(404);
  });
});
