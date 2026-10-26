// Review's "Snap cover" end to end without a camera: the shutter picture goes out as a
// cover read for the bound row, the row upgrades in place, and the scanner leaves.
import { act, fireEvent, screen, waitFor } from '@testing-library/react-native';
import type { CandidateDto } from '@/api/generated/models';
import { candidate } from '@/features/review/testData';
import { MOCK_LIBRARY_ID } from '@/mocks/data';
import { identifyMock } from '@/mocks/identify';
import { scanMock } from '@/mocks/scan';
import { renderWithProviders } from '@/test/render';
import type { Resizer } from '../autosnap/snapPhoto';
import { useCardsStore } from '../cards/cardsStore';
import { makeKit, readItem, readingOf, resetKit } from '../pipeline/testKit';
import { DEV_CODES } from './devCodes';
import { ScannerScreen } from './ScannerScreen';
import { FakeScannerSource } from './ScannerSource';

const mockRouter = { push: jest.fn(), back: jest.fn(), replace: jest.fn() };
jest.mock('expo-router', () => ({ useRouter: () => mockRouter }));

const SESSION = 'b0000000-0000-4000-8000-000000000001';
const tuning = { retryMs: 20, pollMs: 20, resendMs: 40, flashMs: 50 };
const resize: Resizer = async (photo, target) => ({ uri: `${photo.uri}#resized`, width: target?.width ?? photo.width, height: target?.height ?? photo.height });

const seed = (list: CandidateDto[]) => {
  scanMock.sessions.set(SESSION, { id: SESSION, libraryId: MOCK_LIBRARY_ID, seq: 100, candidates: list, answers: new Map(), status: 'open' });
};

beforeEach(() => {
  scanMock.reset();
  identifyMock.reset();
  useCardsStore.getState().reset();
});

afterEach(() => {
  resetKit();
  scanMock.reset();
  identifyMock.reset();
});

describe('bound cover', () => {
  it('sends the shutter picture as a cover for the row, upgrades it in place, and leaves', async () => {
    const row = candidate({ resolution: 'not_found', release: null, source: 'picture_apple_model', rawCode: null, visibleText: 'Dune Mess' });
    seed([row]);
    const kit = makeKit();
    kit.reader.script(readingOf([readItem('Dune')]));
    kit.tray.getState().setSessionId(SESSION);
    const onDone = jest.fn();
    await renderWithProviders(
      <ScannerScreen
        libraryId={MOCK_LIBRARY_ID}
        source={new FakeScannerSource()}
        pipeline={kit.deps}
        tuning={tuning}
        autoSnapTuning={{ flashMs: 50, resize }}
        boundScan={{ sessionId: SESSION, candidateId: row.id, title: 'Dune Mess', kind: 'cover', onDone, onCancel: jest.fn() }}
      />,
    );
    expect(await screen.findByText('Snap the cover for Dune Mess')).toBeTruthy();

    await act(async () => {
      fireEvent.press(screen.getByLabelText('Take picture'));
    });

    await waitFor(() => expect(onDone).toHaveBeenCalledTimes(1));
    const sent = identifyMock.requests.at(-1)!;
    expect(sent.body.kind).toBe('cover');
    expect(sent.body.candidateId).toBe(row.id);
    expect(sent.body.items).toHaveLength(1);
    const upgraded = useCardsStore.getState().byId[row.id]!;
    expect(upgraded.id).toBe(row.id);
    expect(upgraded.resolution).toBe('matched');
    expect(upgraded.release?.title).toBe('Dune');
    expect(scanMock.sessions.get(SESSION)!.candidates).toHaveLength(1);
  });

  it('ignores stray codes while waiting for the cover', async () => {
    const row = candidate({ resolution: 'not_found', release: null, source: 'picture_apple_model', rawCode: null, visibleText: 'Dune Mess' });
    seed([row]);
    const kit = makeKit();
    kit.tray.getState().setSessionId(SESSION);
    const source = new FakeScannerSource();
    await renderWithProviders(
      <ScannerScreen
        libraryId={MOCK_LIBRARY_ID}
        source={source}
        pipeline={kit.deps}
        tuning={tuning}
        autoSnapTuning={{ flashMs: 50, resize }}
        boundScan={{ sessionId: SESSION, candidateId: row.id, title: 'Dune Mess', kind: 'cover', onDone: jest.fn(), onCancel: jest.fn() }}
      />,
    );
    expect(await screen.findByText('Snap the cover for Dune Mess')).toBeTruthy();

    await act(async () => source.inject([{ value: DEV_CODES.mario64New, symbology: 'ean-13' }]));

    expect(scanMock.barcodeRequests).toHaveLength(0);
    expect(kit.tray.getState().lines).toHaveLength(0);
    expect(scanMock.sessions.get(SESSION)!.candidates).toHaveLength(1);
  });

  it('cancelling sends nothing and leaves the row alone', async () => {
    const row = candidate({ resolution: 'not_found', release: null, source: 'picture_apple_model', rawCode: null, visibleText: 'Dune Mess' });
    seed([row]);
    const kit = makeKit();
    kit.tray.getState().setSessionId(SESSION);
    const onCancel = jest.fn();
    await renderWithProviders(
      <ScannerScreen
        libraryId={MOCK_LIBRARY_ID}
        source={new FakeScannerSource()}
        pipeline={kit.deps}
        tuning={tuning}
        autoSnapTuning={{ flashMs: 50, resize }}
        boundScan={{ sessionId: SESSION, candidateId: row.id, title: 'Dune Mess', kind: 'cover', onDone: jest.fn(), onCancel }}
      />,
    );

    fireEvent.press(await screen.findByLabelText('Cancel'));

    expect(onCancel).toHaveBeenCalledTimes(1);
    expect(identifyMock.requests).toHaveLength(0);
    expect(scanMock.sessions.get(SESSION)!.candidates[0]).toMatchObject({ id: row.id, resolution: 'not_found' });
  });
});
