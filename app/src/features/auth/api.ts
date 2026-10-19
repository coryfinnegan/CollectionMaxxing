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

const listFrom = async (url: string): Promise<SessionDto[]> =>
  (await customFetch<{ data: { items: SessionDto[] } }>(url)).data.items;

export type SessionList = {
  sessions: SessionDto[];
  /** Which half of the list could not be loaded, when the other half did. */
  failed: ('phones' | 'browsers')[];
};

/**
 * Where you are signed in: paired phones (`kind: 'device'`) and browsers (`kind: 'web'`), most recently seen first. If one of the
 * two lists fails, the other is still returned and `failed` names the missing half; if both fail, this throws.
 */
export const listSessions = async (): Promise<SessionList> => {
  const [devices, browsers] = await Promise.allSettled([listFrom('/api/v1/me/sessions'), listFrom('/api/v1/me/web-sessions')]);
  if (devices.status === 'rejected' && browsers.status === 'rejected') throw devices.reason;
  const failed: SessionList['failed'] = [];
  if (devices.status === 'rejected') failed.push('phones');
  if (browsers.status === 'rejected') failed.push('browsers');
  const seen = (s: SessionDto) => new Date(s.lastSeenAt ?? s.createdAt).getTime();
  const sessions = [...(devices.status === 'fulfilled' ? devices.value : []), ...(browsers.status === 'fulfilled' ? browsers.value : [])];
  return { sessions: sessions.sort((a, b) => seen(b) - seen(a)), failed };
};

/** Signs one phone or browser out. */
export const revokeSession = ({ id, kind }: Pick<SessionDto, 'id' | 'kind'>) =>
  customFetch(`/api/v1/me/${kind === 'web' ? 'web-sessions' : 'sessions'}/${encodeURIComponent(id)}`, { method: 'DELETE' });

/** Signs every browser of this person out, this one included. Paired phones keep their own sign-in. */
export const revokeWebSessions = () => customFetch('/api/v1/me/web-sessions', { method: 'DELETE' });

export const pairDevice = () => post<PairDeviceDto>('/api/v1/me/devices/pair');
