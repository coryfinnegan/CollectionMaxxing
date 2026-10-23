// The new account and library calls from docs/15 "API contract", typed by hand until ONB-1 (#249) merges and the generated client
// covers them. Swap this module for the generated hooks then: every caller goes through ./hooks, so nothing else changes.
import type { LibraryDto, LibraryRole, LibrarySummaryDto } from '@/api/generated/models';
import { ApiError, customFetch } from '@/lib/http';

export type CreateLibraryRequest = { name: string };

/** A pasted link, a link token or a code. */
export type InviteLookupRequest = { invite: string };

export type InvitePreviewDto = { ownerName: string; libraryName: string; role: LibraryRole; expiresAt: string };

/** `CreatedInviteDto` gains `code` ("ABCD-EFGH-JK"); the generated type does not have it yet. */
export type WithInviteCode = { code?: string | null };

const send = async <T>(url: string, method: 'POST' | 'DELETE', body?: object): Promise<T> =>
  (await customFetch<{ data: T }>(url, { method, ...(body ? { body: JSON.stringify(body) } : {}) })).data;

/** `POST /api/v1/libraries`: a library with you as its Owner. */
export const createLibrary = (request: CreateLibraryRequest) => send<LibraryDto>('/api/v1/libraries', 'POST', request);

/** `POST /api/v1/invites:preview` */
export const previewInvite = (request: InviteLookupRequest) => send<InvitePreviewDto>('/api/v1/invites:preview', 'POST', request);

/** `POST /api/v1/invites:accept`: joins with exactly the invited role. */
export const acceptInvite = (request: InviteLookupRequest) => send<LibrarySummaryDto>('/api/v1/invites:accept', 'POST', request);

/** `DELETE /api/v1/libraries/{libraryId}/membership`: you leave; the account stays. */
export const leaveLibrary = async (libraryId: string): Promise<void> => {
  await send<undefined>(`/api/v1/libraries/${encodeURIComponent(libraryId)}/membership`, 'DELETE');
};

/** The machine-readable problem code ("invite.invalid"), from `code` or the end of `type`. */
export function problemCode(error: unknown): string | undefined {
  if (!(error instanceof ApiError)) return undefined;
  return error.problem.code ?? error.problem.type?.split('/').pop() ?? undefined;
}
