// The words for creating, joining and leaving a library (docs/15 "What you see"). Errors say what happened and what to do next.
import { ApiError } from '@/lib/http';
import { friendlyError } from '@/lib/problem';
import { problemCode } from './api';

export const invalidInviteCopy = "That invite doesn't work. Ask for a new one.";
export const limitReachedCopy = 'You already own 3 libraries, which is the most you can have.';
export const joinPrompt = 'Paste the invite link or type the code you were sent.';

export function createLibraryError(error: unknown): string {
  const code = problemCode(error);
  if (error instanceof ApiError && error.status === 409 && code === 'library.limit_reached') return limitReachedCopy;
  if (code === 'library.name_required') return 'Give your library a name.';
  if (code === 'library.name_too_long') return 'Use 80 characters or fewer for the name.';
  return friendlyError(error);
}

/** Unknown, expired, revoked and used invites all get the same answer, and so does this copy. */
export function inviteError(error: unknown): string {
  const code = problemCode(error);
  if (error instanceof ApiError && error.status === 404) return invalidInviteCopy;
  if (error instanceof ApiError && error.status === 409 && code === 'invite.already_member') return "You're already in that library.";
  return friendlyError(error);
}

export function leaveLibraryError(error: unknown): string {
  if (problemCode(error) === 'library.last_owner') return "A library needs an owner, so its last owner can't leave.";
  return friendlyError(error);
}

export const leaveLibraryWarning = (libraryName: string) => `You'll lose access to ${libraryName}. Your account stays.`;
