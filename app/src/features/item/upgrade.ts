// Verifying an item's edition from its barcode (design §11): the bound read goes to :rematch, which swaps
// the release in place and verifies the edition — or refuses naming the barcode's real owner, and then
// nothing changes. The refusal's ApiError carries the server's words for the scanner to show.
import { rematchItem } from '@/api/generated/items/items';
import type { ItemDto } from '@/api/generated/models';
import { ApiError } from '@/lib/http';

/** Posts the bound barcode for the item and returns the upgraded item. Throws the refusal untouched. */
export async function submitItemBarcodeUpgrade(libraryId: string, itemId: string, barcode: string): Promise<ItemDto> {
  const response = await rematchItem(libraryId, itemId, { barcode });
  if (response.status !== 200) throw new ApiError(response.status, {});
  return response.data;
}
