// "A newer version is ready" for the sideloaded iPhone app. The server says which app version it is serving (the info route's
// `version`); the app asks at launch and when it comes to the foreground, at most once a day, and remembers the answer.
// No answer (offline, a busy server, no .ipa yet) is silent. The web is always current and never asks.
import { useEffect, useReducer } from 'react';
import { AppState } from 'react-native';
import { getIpaInfo } from '@/api/generated/app/app';
import { apiBaseUrl } from '@/lib/http';
import { isWeb } from '@/lib/auth/session';
import { isNewerVersion } from './semver';
import { updateEnvironment } from './services';

const DAY_MS = 24 * 60 * 60 * 1000;
const CHECKED_AT = 'update.checkedAt';
const SERVER_VERSION = 'update.serverVersion';
const DISMISSED_VERSION = 'update.dismissedVersion';
const DOWNLOAD_PATH = '/api/v1/app/ipa';

export type UpdateNotice = {
  /** The version that is ready, for example "1.0.2". */
  version: string;
  /** Opens the server's download in Safari. */
  download: () => void;
  /** Hides the notice for this version; the next version shows it again. */
  dismiss: () => void;
};

let pending: Promise<void> | null = null;

/** Asks the server which version it serves, unless it was asked within the last day. Never throws. */
function refreshServerVersion(): Promise<void> {
  pending ??= (async () => {
    const { store, now } = updateEnvironment();
    const last = Number(store.getString(CHECKED_AT));
    const at = now();
    if (store.getString(CHECKED_AT) && Number.isFinite(last) && at >= last && at - last < DAY_MS) return;
    try {
      const response = await getIpaInfo();
      if (response.status !== 200) return;
      const version = response.data.available ? response.data.version : null;
      if (typeof version === 'string') store.set(SERVER_VERSION, version);
      else store.remove(SERVER_VERSION);
      store.set(CHECKED_AT, String(at));
    } catch {
      // Offline or the server is having a moment: say nothing, keep what is remembered and ask again at the next foreground.
    }
  })().finally(() => {
    pending = null;
  });
  return pending;
}

function readNotice(): string | null {
  const { store, appVersion } = updateEnvironment();
  const served = store.getString(SERVER_VERSION);
  if (!served || !isNewerVersion(served, appVersion())) return null;
  const dismissed = store.getString(DISMISSED_VERSION);
  return dismissed && !isNewerVersion(served, dismissed) ? null : served;
}

export function useUpdateNotice(): UpdateNotice | null {
  const native = !isWeb();
  const [, refresh] = useReducer((n: number) => n + 1, 0);

  useEffect(() => {
    if (!native) return;
    let mounted = true;
    const check = () => {
      void refreshServerVersion().then(() => mounted && refresh());
    };
    check();
    const subscription = AppState.addEventListener('change', (state) => {
      if (state === 'active') check();
    });
    return () => {
      mounted = false;
      subscription.remove();
    };
  }, [native]);

  const version = native ? readNotice() : null;
  if (!version) return null;
  return {
    version,
    download: () => void updateEnvironment().open(`${apiBaseUrl()}${DOWNLOAD_PATH}`).catch(() => {}),
    dismiss: () => {
      updateEnvironment().store.set(DISMISSED_VERSION, version);
      refresh();
    },
  };
}
