import { MemoryTokenStore, SecureTokenStore } from './tokenStore';

const creds = { refreshToken: 'r', serverUrl: 'https://pc.tailnet.ts.net' };

describe('token stores', () => {
  it('memory store saves, loads and clears', async () => {
    const store = new MemoryTokenStore();
    expect(await store.load()).toBeNull();
    await store.save(creds);
    expect(await store.load()).toEqual(creds);
    await store.clear();
    expect(await store.load()).toBeNull();
  });

  it('secure store keeps credentials in the keychain wrapper and ignores garbage', async () => {
    const data = new Map<string, string>();
    const store = new SecureTokenStore({
      getItemAsync: async (k) => data.get(k) ?? null,
      setItemAsync: async (k, v) => void data.set(k, v),
      deleteItemAsync: async (k) => void data.delete(k),
    });
    await store.save(creds);
    expect(await store.load()).toEqual(creds);
    data.set('cm.credentials', '{not json');
    expect(await store.load()).toBeNull();
    await store.clear();
    expect(data.size).toBe(0);
  });

  it('secure store asks the keychain for this-device-only access, so the token never leaves the phone in a backup', async () => {
    const seen: unknown[] = [];
    const store = new SecureTokenStore(
      {
        getItemAsync: async () => null,
        setItemAsync: async (_k, _v, options) => void seen.push(options),
        deleteItemAsync: async () => undefined,
      },
      { keychainAccessible: 42 },
    );
    await store.save(creds);
    expect(seen).toEqual([{ keychainAccessible: 42 }]);
  });
});
