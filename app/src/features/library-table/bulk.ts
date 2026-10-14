// Pure helpers for bulk actions: the 500-id cap and the plain-language result copy.
export const BULK_LIMIT = 500;

export function capForBulk(ids: readonly string[]): { ids: string[]; message?: string } {
  if (ids.length <= BULK_LIMIT) return { ids: [...ids] };
  return {
    ids: ids.slice(0, BULK_LIMIT),
    message: `${ids.length} selected. Bulk actions change ${BULK_LIMIT} at a time, so they apply to the first ${BULK_LIMIT}.`,
  };
}

const items = (n: number) => `${n} ${n === 1 ? 'item' : 'items'}`;

/** `verb` is past tense and capitalised: "Moved", "Deleted", "Updated". */
export function resultMessage(verb: string, requested: number, affected: number): { ok: boolean; text: string } {
  const advice = "Reload the list to see what's there now.";
  if (affected >= requested) return { ok: true, text: `${verb} ${items(requested)}.` };
  if (affected <= 0) {
    return { ok: false, text: `Nothing changed. These items were deleted or changed by someone else. ${advice}` };
  }
  return {
    ok: false,
    text: `${verb} ${affected} of ${items(requested)}. The other ${requested - affected} were deleted or changed by someone else. ${advice}`,
  };
}
