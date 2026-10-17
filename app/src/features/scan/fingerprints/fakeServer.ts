// The PC's side of the backup, for tests: PUT keeps what it is given, GET pages it back the way the real endpoint does.
import { HttpResponse, http } from 'msw';
import { server } from '@/mocks/server';

export type WirePrint = { itemId: string; releaseId: string; revision: number; data: string; createdAt: string };

export class FakePrintsServer {
  readonly puts: { prints: WirePrint[] }[] = [];
  readonly gets: { since: string | null; cursor: string | null; limit: string | null }[] = [];
  /** The rows the PC holds, oldest first. */
  rows: WirePrint[] = [];
  /** The next PUTs fail with a 500. */
  failPuts = 0;
  pageSize = 200;

  readonly handlers = [
    http.put('*/api/v1/libraries/:libraryId/feature-prints', async ({ request }) => {
      const body = (await request.json()) as { prints: WirePrint[] };
      this.puts.push(body);
      if (this.failPuts > 0) {
        this.failPuts -= 1;
        return HttpResponse.json({ title: 'Server problem', status: 500 }, { status: 500, headers: { 'Content-Type': 'application/problem+json' } });
      }
      this.rows.push(...body.prints);
      return new HttpResponse(null, { status: 204 });
    }),
    http.get('*/api/v1/libraries/:libraryId/feature-prints', ({ request }) => {
      const url = new URL(request.url);
      const since = url.searchParams.get('since');
      const cursor = url.searchParams.get('cursor');
      this.gets.push({ since, cursor, limit: url.searchParams.get('limit') });
      const start = cursor ? Number(cursor) : 0;
      const rows = this.rows.filter((r) => !since || r.createdAt > since).slice(start);
      const items = rows.slice(0, this.pageSize);
      return HttpResponse.json({ items, nextCursor: rows.length > items.length ? String(start + items.length) : null });
    }),
  ];

  use() {
    server.use(...this.handlers);
    return this;
  }
}
