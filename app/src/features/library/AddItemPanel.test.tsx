import { fireEvent, screen, waitFor } from '@testing-library/react-native';
import { HttpResponse, http } from 'msw';
import { localDateString } from '@/lib/dates';
import { MOCK_LIBRARY_ID } from '@/mocks/data';
import { server } from '@/mocks/server';
import { MemoryKeyValueStore } from '@/features/scan/tray/trayStore';
import { setRecentStorage } from '@/features/search/recent';
import { renderWithProviders } from '@/test/render';
import { AddItemPanel } from './AddItemPanel';

const ALIEN_VHS = 'f1000000-0000-4000-8000-000000000002';

beforeEach(() => setRecentStorage(new MemoryKeyValueStore()));
afterAll(() => setRecentStorage(undefined));

// Every create carries an Idempotency-Key; the server replays the first payload a key
// was sent with and rejects any other payload under the same key (422).
const captureKeys = () => {
  const sent: { key: string | null; body: string }[] = [];
  server.use(
    http.post('*/api/v1/libraries/:libraryId/items', async ({ request }) => {
      sent.push({ key: request.headers.get('Idempotency-Key'), body: await request.text() });
      return HttpResponse.json(
        { title: 'Already in your library.', status: 409 },
        { status: 409, headers: { 'Content-Type': 'application/problem+json' } },
      );
    }),
  );
  return sent;
};

async function addManual(title: string) {
  await fireEvent.changeText(screen.getByLabelText('Title'), title);
  await fireEvent.press(screen.getByLabelText('Add to library'));
  await screen.findByText('Already in your library.');
}

describe('AddItemPanel idempotency keys', () => {
  it('sends a different item under a new key after a failed add', async () => {
    const sent = captureKeys();
    await renderWithProviders(<AddItemPanel libraryId={MOCK_LIBRARY_ID} onClose={() => {}} />);
    await fireEvent.press(screen.getByLabelText('None of these? Add it yourself'));

    await addManual('First go');
    await addManual('Second go');

    expect(sent).toHaveLength(2);
    expect(sent[0]!.key).toBeTruthy();
    expect(sent[1]!.key).toBeTruthy();
    expect(sent[1]!.key).not.toBe(sent[0]!.key);
  });

  it('retries the same item under the same key, so the retry stays idempotent', async () => {
    const sent = captureKeys();
    await renderWithProviders(<AddItemPanel libraryId={MOCK_LIBRARY_ID} onClose={() => {}} />);
    await fireEvent.press(screen.getByLabelText('None of these? Add it yourself'));

    await addManual('Same title');
    await addManual('Same title');

    expect(sent).toHaveLength(2);
    expect(sent[1]!.key).toBe(sent[0]!.key);
  });

  it('sends unknown when no format is picked, never other', async () => {
    const sent = captureKeys();
    await renderWithProviders(<AddItemPanel libraryId={MOCK_LIBRARY_ID} onClose={() => {}} />);
    await fireEvent.press(screen.getByLabelText('None of these? Add it yourself'));

    await addManual('No format picked');

    expect(sent).toHaveLength(1);
    expect(JSON.parse(sent[0]!.body).manual.format).toBe('unknown');
  });
});

describe('AddItemPanel Search view adoption (#458)', () => {
  it('adds the picked edition from the Search view', async () => {
    const sent: string[] = [];
    server.use(
      http.post('*/api/v1/libraries/:libraryId/items', async ({ request }) => {
        sent.push(await request.text());
        return HttpResponse.json({}, { status: 201 });
      }),
    );
    const onClose = jest.fn();
    await renderWithProviders(<AddItemPanel libraryId={MOCK_LIBRARY_ID} onClose={onClose} />);
    await fireEvent.changeText(await screen.findByLabelText('Search the catalog'), 'alien');
    await fireEvent.press(await screen.findByRole('button', { name: 'Alien, Ridley Scott · 1979–1980 · 2 editions' }));
    await fireEvent.press(await screen.findByRole('button', { name: 'Alien (1980), VHS' }));
    await waitFor(() => expect(onClose).toHaveBeenCalledTimes(1));
    expect(sent).toHaveLength(1);
    expect(JSON.parse(sent[0]!)).toMatchObject({ releaseId: ALIEN_VHS, addedVia: 'search' });
  });

  it('adds the provider-only result you picked by its provider and id, not by the shared empty id', async () => {
    const sent: string[] = [];
    server.use(
      http.post('*/api/v1/libraries/:libraryId/items', async ({ request }) => {
        sent.push(await request.text());
        return HttpResponse.json({}, { status: 201 });
      }),
    );
    const onClose = jest.fn();
    await renderWithProviders(<AddItemPanel libraryId={MOCK_LIBRARY_ID} onClose={onClose} />);
    await fireEvent.changeText(await screen.findByLabelText('Search the catalog'), 'doom');
    await fireEvent.press(await screen.findByRole('button', { name: 'Doom II, id Software · 1994 · 1 edition' }));
    await fireEvent.press(await screen.findByRole('button', { name: 'Doom II (1994), Game disc, GT Interactive' }));
    await waitFor(() => expect(onClose).toHaveBeenCalledTimes(1));
    expect(sent).toHaveLength(1);
    const body = JSON.parse(sent[0]!) as Record<string, unknown>;
    expect(body).toMatchObject({ providerRef: { provider: 'igdb', externalId: 'doom-ii-1994' }, addedVia: 'search' });
    expect(body).not.toHaveProperty('releaseId');
  });

  it('adds a provider-only result through the mock API and closes', async () => {
    const onClose = jest.fn();
    await renderWithProviders(<AddItemPanel libraryId={MOCK_LIBRARY_ID} onClose={onClose} />);
    await fireEvent.changeText(await screen.findByLabelText('Search the catalog'), 'doom');
    await fireEvent.press(await screen.findByRole('button', { name: 'Doom 3, id Software · 2004 · 1 edition' }));
    await fireEvent.press(await screen.findByRole('button', { name: 'Doom 3 (2004), Game disc, Activision' }));
    await waitFor(() => expect(onClose).toHaveBeenCalledTimes(1));
  });

  it('says an expired result expired, and asks the search again so the retry works', async () => {
    server.use(
      http.post(
        '*/api/v1/libraries/:libraryId/items',
        () =>
          HttpResponse.json(
            { title: 'Not found', status: 404, code: 'provider_hit.not_found' },
            { status: 404, headers: { 'Content-Type': 'application/problem+json' } },
          ),
        { once: true },
      ),
    );
    const searches: string[] = [];
    const heard = ({ request }: { request: Request }) => {
      if (request.url.includes('/api/v1/catalog/search')) searches.push(request.url);
    };
    server.events.on('request:start', heard);
    try {
      const onClose = jest.fn();
      await renderWithProviders(<AddItemPanel libraryId={MOCK_LIBRARY_ID} onClose={onClose} />);
      await fireEvent.changeText(await screen.findByLabelText('Search the catalog'), 'doom');
      await fireEvent.press(await screen.findByRole('button', { name: 'Doom II, id Software · 1994 · 1 edition' }));
      await screen.findByRole('button', { name: 'Doom II (1994), Game disc, GT Interactive' });
      const asked = searches.length;

      await fireEvent.press(screen.getByRole('button', { name: 'Doom II (1994), Game disc, GT Interactive' }));

      expect(await screen.findByText('That search result has expired. Try adding it again.')).toBeTruthy();
      await waitFor(() => expect(searches.length).toBeGreaterThan(asked));
      expect(onClose).not.toHaveBeenCalled();

      await fireEvent.press(await screen.findByRole('button', { name: 'Doom II (1994), Game disc, GT Interactive' }));
      await waitFor(() => expect(onClose).toHaveBeenCalledTimes(1));
    } finally {
      server.events.removeListener('request:start', heard);
    }
  });

  it('opens the manual form from the no-matches state', async () => {
    const onClose = jest.fn();
    await renderWithProviders(<AddItemPanel libraryId={MOCK_LIBRARY_ID} onClose={onClose} />);
    await fireEvent.changeText(await screen.findByLabelText('Search the catalog'), 'zzz no such title');
    await fireEvent.press(await screen.findByLabelText('Add it yourself'));
    expect(await screen.findByLabelText('Title')).toBeTruthy();
  });
});

describe('AddItemPanel acquired date (#441)', () => {
  it('stamps a new item with the day it was added, without asking', async () => {
    const sent = captureKeys();
    await renderWithProviders(<AddItemPanel libraryId={MOCK_LIBRARY_ID} onClose={() => {}} />);
    await fireEvent.press(screen.getByLabelText('None of these? Add it yourself'));

    await addManual('Fresh find');

    expect(sent).toHaveLength(1);
    expect(JSON.parse(sent[0]!.body).acquiredOn).toBe(localDateString());
  });
});
