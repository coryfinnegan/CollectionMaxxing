// Where the long-lived credentials live. The access token is never stored; it stays in memory (see session.ts).

export type StoredCredentials = { refreshToken: string; serverUrl: string };

export interface TokenStore {
  load(): Promise<StoredCredentials | null>;
  save(credentials: StoredCredentials): Promise<void>;
  clear(): Promise<void>;
}

export class MemoryTokenStore implements TokenStore {
  private credentials: StoredCredentials | null;

  constructor(initial: StoredCredentials | null = null) {
    this.credentials = initial;
  }

  async load() {
    return this.credentials;
  }

  async save(credentials: StoredCredentials) {
    this.credentials = credentials;
  }

  async clear() {
    this.credentials = null;
  }
}

/** The slice of expo-secure-store we use, so this file needs no native package until the lead adds it. */
export interface SecureStoreLike {
  getItemAsync(key: string): Promise<string | null>;
  setItemAsync(key: string, value: string): Promise<void>;
  deleteItemAsync(key: string): Promise<void>;
}

const secureKey = 'cm.credentials';

/** Keychain-backed store: wire it with `new SecureTokenStore(SecureStore)` from expo-secure-store. */
export class SecureTokenStore implements TokenStore {
  private readonly store: SecureStoreLike;

  constructor(store: SecureStoreLike) {
    this.store = store;
  }

  async load() {
    const raw = await this.store.getItemAsync(secureKey);
    if (!raw) return null;
    try {
      const parsed = JSON.parse(raw) as Partial<StoredCredentials>;
      return parsed.refreshToken && parsed.serverUrl
        ? { refreshToken: parsed.refreshToken, serverUrl: parsed.serverUrl }
        : null;
    } catch {
      return null;
    }
  }

  async save(credentials: StoredCredentials) {
    await this.store.setItemAsync(secureKey, JSON.stringify(credentials));
  }

  async clear() {
    await this.store.deleteItemAsync(secureKey);
  }
}
