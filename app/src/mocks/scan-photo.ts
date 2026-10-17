// Mock of POST /scan-sessions/{id}/candidates/{cid}/photo (SCAN-2c, docs/04): the owner's own picture of one item, which becomes
// that item's cover when the scan is added. Like the real API: JPEG only, 4 MB at most, and a viewer cannot send one (403).
import { HttpResponse, http } from 'msw';
import { scanMock } from './scan';

const MAX_BYTES = 4 * 1024 * 1024;

const problem = (status: number, title: string) =>
  HttpResponse.json({ title, status }, { status, headers: { 'Content-Type': 'application/problem+json' } });

export interface PhotoUpload {
  sessionId: string;
  candidateId: string;
  contentType: string;
  /** Bytes of the file part. */
  size: number;
  fileName: string | null;
  /** How many files were in the form. */
  parts: number;
}

export const photoMock = {
  /** Every photo request the server received, so tests can assert exactly what left the phone. */
  uploads: [] as PhotoUpload[],
  /** Set to make the next upload fail with this status. */
  failNext: null as number | null,
  reset() {
    this.uploads = [];
    this.failNext = null;
  },
};

export const handlers = [
  http.post('*/api/v1/scan-sessions/:id/candidates/:cid/photo', async ({ params, request }) => {
    const form = await request.formData();
    const files = [...form.values()].filter((v): v is File => typeof v !== 'string');
    const file = files[0];
    photoMock.uploads.push({
      sessionId: String(params.id),
      candidateId: String(params.cid),
      contentType: file?.type ?? '',
      size: file?.size ?? 0,
      fileName: file?.name ?? null,
      parts: files.length,
    });
    if (scanMock.role === 'viewer') return problem(403, 'Forbidden');
    if (photoMock.failNext) {
      const status = photoMock.failNext;
      photoMock.failNext = null;
      return problem(status, 'That did not work.');
    }
    const candidate = scanMock.sessions.get(String(params.id))?.candidates.find((c) => c.id === params.cid);
    if (!candidate) return problem(404, 'Not found');
    if (!file) return problem(400, 'A photo is required.');
    if (file.type !== 'image/jpeg') return problem(415, 'Only JPEG pictures are accepted.');
    if (file.size > MAX_BYTES) return problem(413, 'That picture is too large.');
    return HttpResponse.json(candidate);
  }),
];
