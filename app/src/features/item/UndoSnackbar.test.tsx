import { act, fireEvent, screen } from '@testing-library/react-native';
import { HttpResponse, http } from 'msw';
import { MOCK_LIBRARY_ID } from '@/mocks/data';
import { resetItems } from '@/mocks/items';
import { server } from '@/mocks/server';
import { renderWithProviders } from '@/test/render';
import { UndoSnackbar } from './UndoSnackbar';
import { useUndoStore } from './useItem';

const MATRIX = 'c0000000-0000-4000-8000-000000000001';
const DUNE = 'c0000000-0000-4000-8000-000000000003';

beforeEach(() => {
  resetItems();
  useUndoStore.setState({ deleted: null });
});

describe('UndoSnackbar', () => {
  it('offers Undo again when something else is deleted after a failed restore', async () => {
    server.use(
      http.post(/\/api\/v1\/libraries\/[^/]+\/items\/([^/]+):restore$/, () =>
        HttpResponse.json({ title: 'Broken', status: 500 }, { status: 500, headers: { 'Content-Type': 'application/problem+json' } }),
      ),
    );
    useUndoStore.setState({ deleted: { libraryId: MOCK_LIBRARY_ID, id: MATRIX, title: 'The Matrix' } });
    await renderWithProviders(<UndoSnackbar />);
    expect(await screen.findByText('Deleted The Matrix')).toBeTruthy();

    await fireEvent.press(screen.getByLabelText('Undo'));

    expect(await screen.findByText("That didn't work. Try again.")).toBeTruthy();
    expect(screen.queryByLabelText('Undo')).toBeNull();

    // A new deletion arrives while the stale error is showing.
    await act(async () => {
      useUndoStore.setState({ deleted: { libraryId: MOCK_LIBRARY_ID, id: DUNE, title: 'Dune' } });
    });

    expect(await screen.findByText('Deleted Dune')).toBeTruthy();
    expect(screen.getByLabelText('Undo')).toBeTruthy();
  });
});
