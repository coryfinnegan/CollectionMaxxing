// When GET /libraries fails, no screen may spin forever: each gate says so and offers Try again (which asks again).
import { fireEvent, screen, waitFor } from '@testing-library/react-native';
import { HttpResponse, http } from 'msw';
import type { ReactElement } from 'react';
import { ItemDetailScreen } from '@/features/item/ItemDetailScreen';
import { ItemEditScreen } from '@/features/item/ItemEditScreen';
import { LibraryGrid } from '@/features/library/LibraryGrid';
import { LibraryTable } from '@/features/library-table/LibraryTable.web';
import { LocationsScreen } from '@/features/locations/LocationsScreen';
import { MembersScreen } from '@/features/members/MembersScreen';
import { MeScreen } from '@/features/me/MeScreen';
import { RecentlyDeletedScreen } from '@/features/me/RecentlyDeletedScreen';
import { server } from '@/mocks/server';
import { renderWithProviders } from '@/test/render';

// Keep the retry itself (the failure only shows once it gives up), but not the seconds of waiting.
jest.mock('./librariesRetry', () => ({ ...jest.requireActual('./librariesRetry'), librariesRetryDelay: () => 1 }));

const mockRouter = { push: jest.fn(), back: jest.fn(), replace: jest.fn(), canGoBack: jest.fn(() => true) };
jest.mock('expo-router', () => ({ useRouter: () => mockRouter, usePathname: () => '/library' }));

const MATRIX = 'c0000000-0000-4000-8000-000000000001';

let asked = 0;
let failing = true;

beforeEach(() => {
  asked = 0;
  failing = true;
  server.use(
    http.get('*/api/v1/libraries', () => {
      asked += 1;
      return failing ? new HttpResponse(null, { status: 503 }) : HttpResponse.json({ items: [{ id: '11111111-1111-4111-8111-111111111111', name: "Cory's library", role: 'owner' }], nextCursor: null });
    }),
  );
});

const screens: [string, () => ReactElement][] = [
  ['the library grid', () => <LibraryGrid title="Library" />],
  ['the library table', () => <LibraryTable title="Library" />],
  ['the item screen', () => <ItemDetailScreen id={MATRIX} />],
  ['the item edit screen', () => <ItemEditScreen id={MATRIX} />],
  ['Locations', () => <LocationsScreen />],
  ['Members', () => <MembersScreen />],
  ['Recently deleted', () => <RecentlyDeletedScreen />],
];

describe('a failed libraries list', () => {
  it.each(screens)('is said plainly on %s, with Try again', async (_name, element) => {
    await renderWithProviders(element());

    expect(await screen.findByText("Couldn't load your library")).toBeTruthy();
    expect(screen.getByLabelText('Try again')).toBeTruthy();
    expect(screen.queryByTestId('loading-state')).toBeNull();
  });

  it('is said on Me too, without hiding where Me leads', async () => {
    await renderWithProviders(<MeScreen />);

    expect(await screen.findByText("Couldn't load your library")).toBeTruthy();
    expect(screen.getByLabelText('Try again')).toBeTruthy();
    expect(screen.getByText('Locations')).toBeTruthy();
  });

  it('is asked again on Try again, and the screen loads once it answers', async () => {
    await renderWithProviders(<LocationsScreen />);
    await screen.findByText("Couldn't load your library");
    const before = asked;

    failing = false;
    await fireEvent.press(screen.getByLabelText('Try again'));

    expect(await screen.findByText('Game room')).toBeTruthy();
    expect(asked).toBeGreaterThan(before);
    expect(screen.queryByText("Couldn't load your library")).toBeNull();
  });

  it('tries a couple of times before it gives up', async () => {
    await renderWithProviders(<LocationsScreen />);
    await screen.findByText("Couldn't load your library");

    await waitFor(() => expect(asked).toBe(3));
  });

  it('keeps the loading state while it is still trying', async () => {
    server.use(http.get('*/api/v1/libraries', () => new Promise<never>(() => undefined)));
    await renderWithProviders(<LocationsScreen />);

    expect(await screen.findByText('Loading your locations…')).toBeTruthy();
    expect(screen.queryByText("Couldn't load your library")).toBeNull();
  });
});
