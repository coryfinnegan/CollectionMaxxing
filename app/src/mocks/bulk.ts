// Mock bulk endpoint (POST …/items:bulk) and a small tag list for the "Add tag" action.
// Like the real API: at most 500 ids, patch or delete, and `affected` counts only the live items it changed.
import { HttpResponse, http } from 'msw';
import type { BulkItemsRequest, TagDto } from '@/api/generated/models';
import { itemsState } from './items';

export const mockTags: TagDto[] = [
  { id: 'f0000000-0000-4000-8000-000000000001', name: 'Favourites', itemCount: 0 },
  { id: 'f0000000-0000-4000-8000-000000000002', name: 'To lend', itemCount: 0 },
];

const problem = (status: number, title: string) =>
  HttpResponse.json({ title, status }, { status, headers: { 'Content-Type': 'application/problem+json' } });

export const handlers = [
  http.post(/\/api\/v1\/libraries\/[^/]+\/items:bulk$/, async ({ request }) => {
    const body = (await request.json()) as BulkItemsRequest;
    const ids = body.ids ?? [];
    if (ids.length === 0) return problem(400, 'Choose at least one item.');
    if (ids.length > 500) return problem(400, 'Bulk actions take at most 500 items.');
    const entries = itemsState.entries.filter((e) => ids.includes(e.item.id) && !e.item.deletedAt);
    for (const entry of entries) {
      if (body.action === 'delete') {
        entry.item.deletedAt = new Date().toISOString();
      } else if (body.patch) {
        if (body.patch.status) entry.item.status = body.patch.status;
        if (body.patch.locationId !== undefined) entry.item.locationId = body.patch.locationId;
        const added = body.patch.addTagIds ?? [];
        entry.item.tagIds = [...new Set([...entry.item.tagIds, ...added])].filter((t) => !body.patch?.removeTagIds?.includes(t));
      }
      entry.item.updatedAt = new Date().toISOString();
      entry.version += 1;
    }
    return HttpResponse.json({ affected: entries.length });
  }),

  http.get('*/api/v1/libraries/:libraryId/tags', () => HttpResponse.json({ items: mockTags, nextCursor: null })),
];
