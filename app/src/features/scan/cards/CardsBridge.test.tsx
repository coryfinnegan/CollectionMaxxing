// When the library changes, the result cards on screen belong to the old scan: they are dropped, so Library B
// never shows Library A's cards. (Reopening that scan loads its session again, so nothing is lost.)
import { useQueryClient } from '@tanstack/react-query';
import { act, screen, waitFor } from '@testing-library/react-native';
import { Text } from 'react-native';
import { MOCK_LIBRARY_ID, mockAccount } from '@/mocks/data';
import { useActiveLibraryId } from '@/features/locations/useLocations';
import { candidate } from '@/features/review/testData';
import { librariesQueryKey } from '@/features/onboarding/useLibraries';
import { useLibraryStore } from '@/state/library';
import { renderWithProviders } from '@/test/render';
import { CardsBridge } from './CardsBridge';
import { useCardsStore } from './cardsStore';

const SECOND = 'f0000000-0000-4000-8000-000000000002';

function Probe() {
  const { libraryId } = useActiveLibraryId();
  return <Text>{libraryId ?? 'none'}</Text>;
}

function Refresher({ onRefresh }: { onRefresh: (refresh: () => Promise<void>) => void }) {
  const client = useQueryClient();
  onRefresh(async () => void (await client.invalidateQueries({ queryKey: librariesQueryKey() })));
  return null;
}

beforeEach(() => {
  mockAccount.libraries = [
    { id: MOCK_LIBRARY_ID, name: 'A', role: 'owner' },
    { id: SECOND, name: 'B', role: 'owner' },
  ];
  useLibraryStore.setState({ activeLibraryId: MOCK_LIBRARY_ID });
  useCardsStore.getState().reset();
});

afterEach(() => {
  useCardsStore.getState().reset();
  useLibraryStore.setState({ activeLibraryId: null });
});

describe('cards bridge', () => {
  it('drops the old scan’s cards when the library changes, and keeps them when it does not', async () => {
    let refresh!: () => Promise<void>;
    await renderWithProviders(
      <>
        <CardsBridge />
        <Probe />
        <Refresher onRefresh={(r) => (refresh = r)} />
      </>,
    );
    await screen.findByText(MOCK_LIBRARY_ID);
    useCardsStore.getState().merge([candidate({ rawCode: 'from-A' })]);
    expect(Object.keys(useCardsStore.getState().byId)).toHaveLength(1);

    await act(async () => useLibraryStore.setState({ activeLibraryId: SECOND }));
    await waitFor(() => expect(Object.keys(useCardsStore.getState().byId)).toHaveLength(0));

    useCardsStore.getState().merge([candidate({ rawCode: 'from-B' })]);
    // The list reloads with the same libraries: the bridge re-renders, the id is unchanged, the cards stay.
    await act(async () => {
      await refresh();
    });
    expect(Object.values(useCardsStore.getState().byId).map((c) => c.rawCode)).toEqual(['from-B']);
  });
});
