import { fireEvent, screen } from '@testing-library/react-native';
import { HttpResponse, http } from 'msw';
import { MOCK_LIBRARY_ID } from '@/mocks/data';
import { server } from '@/mocks/server';
import { renderWithProviders } from '@/test/render';
import { AddItemPanel } from './AddItemPanel';

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
});
