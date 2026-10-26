// Review's "Scan barcode" end to end without a camera: the bound read upgrades its row in place,
// bypasses the tray, and leaves the scanner.
import { act, fireEvent, screen, waitFor } from '@testing-library/react-native';
import type { StoreApi } from 'zustand/vanilla';
import type { CandidateDto } from '@/api/generated/models';
import { submitBoundBarcode } from '@/features/review/api';
import { candidate } from '@/features/review/testData';
import { MOCK_LIBRARY_ID } from '@/mocks/data';
import { scanMock } from '@/mocks/scan';
import { renderWithProviders } from '@/test/render';
import { useCardsStore } from '../cards/cardsStore';
import { setTrayStore } from '../tray/storage';
import { createTrayStore, MemoryKeyValueStore, type TrayStore } from '../tray/trayStore';
import { DEV_CODES } from './devCodes';
import { ScannerScreen } from './ScannerScreen';
import { FakeScannerSource } from './ScannerSource';

const mockRouter = { push: jest.fn(), back: jest.fn(), replace: jest.fn() };
jest.mock('expo-router', () => ({ useRouter: () => mockRouter }));

const SESSION = 'a0000000-0000-4000-8000-000000000001';
const tuning = { retryMs: 20, pollMs: 20, resendMs: 40, flashMs: 50 };
const code = (value: string) => ({ value, symbology: 'ean-13' });

const seed = (list: CandidateDto[]) => {
  scanMock.sessions.set(SESSION, { id: SESSION, libraryId: MOCK_LIBRARY_ID, seq: 100, candidates: list, answers: new Map(), status: 'open' });
};

let tray: StoreApi<TrayStore>;

beforeEach(() => {
  scanMock.reset();
  useCardsStore.getState().reset();
  tray = createTrayStore({ storage: new MemoryKeyValueStore(), now: () => 1_000 });
  setTrayStore(tray);
});

afterEach(() => {
  scanMock.reset();
  setTrayStore(undefined);
});

describe('bound scan', () => {
  it('sends the stable read bound to the row, upgrades it in place, and leaves', async () => {
    const row = candidate({ resolution: 'not_found', release: null, rawCode: '000111' });
    seed([row]);
    const onDone = jest.fn();
    const source = new FakeScannerSource();
    await renderWithProviders(
      <ScannerScreen
        libraryId={MOCK_LIBRARY_ID}
        source={source}
        tuning={tuning}
        boundScan={{ sessionId: SESSION, candidateId: row.id, title: 'Code 000111', kind: 'barcode', onDone, onCancel: jest.fn() }}
      />,
    );
    expect(await screen.findByText('Scan the barcode for Code 000111')).toBeTruthy();

    await act(async () => source.inject([code(DEV_CODES.mario64New)]));

    await waitFor(() => expect(onDone).toHaveBeenCalledTimes(1));
    const sent = scanMock.barcodeRequests.at(-1)!;
    expect(sent.candidateId).toBe(row.id);
    expect(sent.codes).toEqual([DEV_CODES.mario64New]);
    const upgraded = useCardsStore.getState().byId[row.id]!;
    expect(upgraded.id).toBe(row.id);
    expect(upgraded.resolution).toBe('matched');
    expect(upgraded.release?.title).toBe('Super Mario 64');
    expect(tray.getState().lines).toHaveLength(0);
  });

  it('posts the bound read to the bound session and merges the upgrade', async () => {
    const row = candidate({ resolution: 'ambiguous', release: null, rawCode: '000111' });
    seed([row]);

    const upgraded = await submitBoundBarcode(SESSION, row.id, { value: DEV_CODES.mario64New, symbology: 'ean-13' });

    expect(upgraded.id).toBe(row.id);
    expect(upgraded.release?.title).toBe('Super Mario 64');
    expect(scanMock.barcodeRequests).toHaveLength(1);
    expect(scanMock.barcodeRequests[0]).toMatchObject({ sessionId: SESSION, candidateId: row.id });
    expect(useCardsStore.getState().byId[row.id]?.release?.title).toBe('Super Mario 64');
  });

  it('cancelling sends nothing and leaves the row alone', async () => {
    const row = candidate({ resolution: 'not_found', release: null, rawCode: '000111' });
    seed([row]);
    const onCancel = jest.fn();
    await renderWithProviders(
      <ScannerScreen
        libraryId={MOCK_LIBRARY_ID}
        source={new FakeScannerSource()}
        tuning={tuning}
        boundScan={{ sessionId: SESSION, candidateId: row.id, title: 'Code 000111', kind: 'barcode', onDone: jest.fn(), onCancel }}
      />,
    );

    fireEvent.press(await screen.findByLabelText('Cancel'));

    expect(onCancel).toHaveBeenCalledTimes(1);
    expect(scanMock.barcodeRequests).toHaveLength(0);
    expect(scanMock.sessions.get(SESSION)!.candidates[0]).toMatchObject({ id: row.id, resolution: 'not_found' });
  });
});
