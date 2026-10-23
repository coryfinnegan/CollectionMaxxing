// Create, join and leave, over the generated client (docs/15 "API contract"). `customFetch` throws an `ApiError` for any non-2xx answer,
// so a call that returns is a success, and these hand back just its body. Every caller goes through ./hooks.
import { acceptInvite as acceptInviteCall, previewInvite as previewInviteCall } from '@/api/generated/invites/invites';
import { createLibrary as createLibraryCall, leaveLibrary as leaveLibraryCall } from '@/api/generated/libraries/libraries';
import type { CreateLibraryRequest, InviteLookupRequest, InvitePreviewDto, LibraryDto, LibrarySummaryDto } from '@/api/generated/models';
import { ApiError } from '@/lib/http';

/** `POST /api/v1/libraries`: a library with you as its Owner. */
export const createLibrary = async (request: CreateLibraryRequest): Promise<LibraryDto> => (await createLibraryCall(request)).data as LibraryDto;

/** `POST /api/v1/invites:preview`: a pasted link, a link token or a code. */
export const previewInvite = async (request: InviteLookupRequest): Promise<InvitePreviewDto> =>
  (await previewInviteCall(request)).data as InvitePreviewDto;

/** `POST /api/v1/invites:accept`: joins with exactly the invited role. */
export const acceptInvite = async (request: InviteLookupRequest): Promise<LibrarySummaryDto> =>
  (await acceptInviteCall(request)).data as LibrarySummaryDto;

/** `DELETE /api/v1/libraries/{libraryId}/membership`: you leave; the account stays. */
export const leaveLibrary = async (libraryId: string): Promise<void> => {
  await leaveLibraryCall(libraryId);
};

/** The machine-readable problem code ("invite.invalid"), from `code` or the end of `type`. */
export function problemCode(error: unknown): string | undefined {
  if (!(error instanceof ApiError)) return undefined;
  return error.problem.code ?? error.problem.type?.split('/').pop() ?? undefined;
}
