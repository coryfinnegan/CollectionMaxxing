// Calls to the sign-in, sign-in-method and device endpoints (docs/14 "Web"). The routes are typed by hand in ./contract until the
// generated client covers them.
import { customFetch } from '@/lib/http';
import type {
  AppSignInDto,
  LogoutDto,
  NextStepDto,
  PageOfAppSignInDto,
  PageOfSignInMethodDto,
  SignInMethodDto,
} from './contract';

export type SessionDto = {
  id: string;
  deviceName: string;
  kind: string;
  createdAt: string;
  lastSeenAt?: string | null;
  isCurrent: boolean;
};

const get = async <T>(url: string): Promise<T> => (await customFetch<{ data: T }>(url)).data;
const post = async <T>(url: string, body?: object): Promise<T> =>
  (await customFetch<{ data: T }>(url, { method: 'POST', ...(body ? { body: JSON.stringify(body) } : {}) })).data;

/** First run: the setup code. The answer says where to go next: the sign-in. */
export const startSetup = (code: string) => post<NextStepDto>('/setup', { code });

/** Ends this browser's session. The answer is where to send the browser to end the Auth0 session too. */
export const logout = () => post<LogoutDto>('/account/logout');

export const listSignInMethods = async (): Promise<SignInMethodDto[]> =>
  (await get<PageOfSignInMethodDto>('/api/v1/me/sign-in-methods')).items;

export const removeSignInMethod = (id: string) =>
  customFetch(`/api/v1/me/sign-in-methods/${encodeURIComponent(id)}`, { method: 'DELETE' });

export const listBrowsers = async (): Promise<SessionDto[]> =>
  (await get<{ items: SessionDto[] }>('/api/v1/me/web-sessions')).items;

export const listIphones = async (): Promise<AppSignInDto[]> => (await get<PageOfAppSignInDto>('/api/v1/me/app-sign-ins')).items;

export type DeviceList = {
  browsers: SessionDto[];
  iphones: AppSignInDto[];
  /** Which half of the list could not be loaded, when the other half did. */
  failed: ('iPhones' | 'browsers')[];
};

/** Where you are signed in. If one of the two lists fails the other is still returned and `failed` names the missing half. */
export const listDevices = async (): Promise<DeviceList> => {
  const [browsers, iphones] = await Promise.allSettled([listBrowsers(), listIphones()]);
  if (browsers.status === 'rejected' && iphones.status === 'rejected') throw browsers.reason;
  const failed: DeviceList['failed'] = [];
  if (iphones.status === 'rejected') failed.push('iPhones');
  if (browsers.status === 'rejected') failed.push('browsers');
  return {
    browsers: browsers.status === 'fulfilled' ? browsers.value : [],
    iphones: iphones.status === 'fulfilled' ? iphones.value : [],
    failed,
  };
};

/** Signs one browser out. */
export const revokeBrowser = (id: string) => customFetch(`/api/v1/me/web-sessions/${encodeURIComponent(id)}`, { method: 'DELETE' });

/** Signs every browser of this person out, this one included. iPhones keep their own sign-in. */
export const revokeWebSessions = () => customFetch('/api/v1/me/web-sessions', { method: 'DELETE' });

/** Signs every iPhone of this person out. */
export const signOutAllIphones = () => customFetch('/api/v1/me/app-sign-ins', { method: 'DELETE' });
