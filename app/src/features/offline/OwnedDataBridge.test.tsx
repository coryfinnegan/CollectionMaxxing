import { onlineManager } from '@tanstack/react-query';
import { waitFor } from '@testing-library/react-native';
import { MOCK_LIBRARY_ID } from '@/mocks/data';
import { resetOwnershipMock } from '@/mocks/ownership';
import { useLibraryStore } from '@/state/library';
import { clearLocalData } from '@/features/me/localData';
import { configureAuth, resetAuth } from '@/lib/auth/session';
import { makeQueryClient, renderWithProviders } from '@/test/render';
import { OwnedDataBridge } from './OwnedDataBridge';
import { getOwnedCache, MemoryOwnedStorage, setOwnedStorage } from './ownedCache';

let storage: MemoryOwnedStorage;

beforeEach(() => {
  resetOwnershipMock();
  storage = new MemoryOwnedStorage();
  setOwnedStorage(storage);
  useLibraryStore.setState({ activeLibraryId: MOCK_LIBRARY_ID });
  onlineManager.setOnline(true);
  configureAuth({ platform: 'native' });
});
afterEach(() => {
  resetAuth();
  useLibraryStore.setState({ activeLibraryId: null });
});
afterAll(() => setOwnedStorage(undefined));

const storedKeys = () => [...(storage as unknown as { values: Map<string, string> }).values.keys()];

describe('OwnedDataBridge', () => {
  it('syncs the owned copy once the signed-in tree is mounted', async () => {
    await renderWithProviders(<OwnedDataBridge />);

    await waitFor(() => expect(getOwnedCache(MOCK_LIBRARY_ID).state.syncedAt).not.toBeNull());
    expect(storedKeys().length).toBeGreaterThan(0);
  });

  it('empties the owned copy when the device is unpaired or signed out', async () => {
    await renderWithProviders(<OwnedDataBridge />);
    await waitFor(() => expect(storedKeys().length).toBeGreaterThan(0));

    await clearLocalData(makeQueryClient());

    expect(storedKeys()).toEqual([]);
    expect(getOwnedCache(MOCK_LIBRARY_ID).state.syncedAt).toBeNull();
  });

  it('stops clearing once unmounted', async () => {
    const view = await renderWithProviders(<OwnedDataBridge />);
    await waitFor(() => expect(storedKeys().length).toBeGreaterThan(0));
    view.unmount();

    await clearLocalData(makeQueryClient());

    expect(storedKeys().length).toBeGreaterThan(0);
  });
});
