// After a move, the library lists say where the copy is now (#448), not just the ownership answers.
import { MOCK_LIBRARY_ID } from '@/mocks/data';
import { resetItems } from '@/mocks/items';
import { makeQueryClient } from '@/test/render';
import { refreshAfterMove } from './ownedMove';

beforeEach(() => resetItems());

describe('refreshAfterMove', () => {
  it('invalidates the library list so the moved copy shows its new spot', async () => {
    const client = makeQueryClient();
    const key = ['library-items', MOCK_LIBRARY_ID, { sort: '-addedAt', limit: 30 }];
    client.setQueryData(key, { pages: [{ items: [], nextCursor: null }], pageParams: [undefined] });
    expect(client.getQueryState(key)?.isInvalidated).toBe(false);

    await refreshAfterMove(client, MOCK_LIBRARY_ID);

    expect(client.getQueryState(key)?.isInvalidated).toBe(true);
  });
});
