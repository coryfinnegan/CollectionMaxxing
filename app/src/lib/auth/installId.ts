// A per-install id (not a secret): lets the server tell this iPhone's sign-ins apart in "Devices and browsers".
import * as SecureStore from 'expo-secure-store';

const KEY = 'cm.install-id';

function uuid(): string {
  const c = (globalThis as { crypto?: { randomUUID?: () => string } }).crypto;
  if (c?.randomUUID) return c.randomUUID();
  return 'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, (ch) => {
    const r = Math.floor(Math.random() * 16);
    return (ch === 'x' ? r : (r & 0x3) | 0x8).toString(16);
  });
}

/** The id kept in the Keychain for this install; made on first use. */
export async function getInstallId(): Promise<string> {
  try {
    const existing = await SecureStore.getItemAsync(KEY);
    if (existing) return existing;
  } catch {
    // fall through to a fresh id
  }
  const id = uuid();
  try {
    await SecureStore.setItemAsync(KEY, id);
  } catch {
    // an id that is not kept just means the next launch registers as a new install
  }
  return id;
}
