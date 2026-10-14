// Calls to the sign-in, device and session endpoints (docs/04 "Account and session"). Written against the contract and the
// MSW mocks; the generated client does not cover the browser-facing sign-in routes.
import { customFetch } from '@/lib/http';
import type { PasskeyJson } from './passkeys';

export type SignedIn = { userId: string; displayName: string; libraryId?: string | null };
export type PasskeyOptions = { options: PasskeyJson };
export type SessionDto = {
  id: string;
  deviceName: string;
  kind: string;
  createdAt: string;
  lastSeenAt?: string | null;
  isCurrent: boolean;
};
export type PairDeviceDto = { serverUrl: string; token: string; code: string; expiresAt: string };
export type InvitePreview = { ownerName: string; role: 'viewer' | 'editor' };

const post = async <T>(url: string, body?: object): Promise<T> =>
  (await customFetch<{ data: T }>(url, { method: 'POST', ...(body ? { body: JSON.stringify(body) } : {}) })).data;

/** First run: the setup code and the owner's name, answered with passkey creation options. */
export const startSetup = (code: string, displayName: string) => post<PasskeyOptions>('/setup', { code, displayName });

/** Joining by invite: the invite token and the new member's name. */
export const startJoin = (inviteToken: string, displayName: string) =>
  post<PasskeyOptions>('/account/passkey/creation-options', { inviteToken, displayName });

export const finishRegistration = (credential: PasskeyJson, options: { name?: string; invite?: string } = {}) =>
  post<SignedIn>(`/account/passkey/register${options.invite ? `?invite=${encodeURIComponent(options.invite)}` : ''}`, {
    credential,
    name: options.name,
  });

export const requestLoginOptions = () => post<PasskeyOptions>('/account/passkey/request-options');
export const finishLogin = (credential: PasskeyJson) => post<SignedIn>('/account/login', { credential });
export const logout = () => customFetch('/account/logout', { method: 'POST' });

/** Who is inviting, and as what. Assumed endpoint: the contract lands with ID-3 (see the PR notes). */
export const getInvitePreview = async (token: string): Promise<InvitePreview> =>
  (await customFetch<{ data: InvitePreview }>(`/api/v1/invites/${encodeURIComponent(token)}`)).data;

export const listSessions = async (): Promise<SessionDto[]> =>
  (await customFetch<{ data: { items: SessionDto[] } }>('/api/v1/me/sessions')).data.items;

export const revokeSession = (id: string) => customFetch(`/api/v1/me/sessions/${encodeURIComponent(id)}`, { method: 'DELETE' });

export const pairDevice = () => post<PairDeviceDto>('/api/v1/me/devices/pair');
