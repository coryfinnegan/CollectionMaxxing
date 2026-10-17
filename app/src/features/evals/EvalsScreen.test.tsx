import { act, fireEvent, screen } from '@testing-library/react-native';
import { setPictureFiles } from '@/features/scan/pictureFiles';
import { configureAuth, resetAuth } from '@/lib/auth/session';
import { MemoryTokenStore } from '@/lib/auth/tokenStore';
import { evalsMock } from '@/mocks/evals';
import { renderWithProviders } from '@/test/render';
import { EvalsScreen } from './EvalsScreen';
import { makeKit } from './evalsTestKit';

beforeEach(() => {
  evalsMock.reset();
  configureAuth({ platform: 'native', tokenStore: new MemoryTokenStore({ refreshToken: 'mock-refresh', serverUrl: 'http://api.test' }) });
});
afterEach(() => {
  resetAuth();
  setPictureFiles(undefined);
});

describe('EvalsScreen', () => {
  it('shows the progress, then the PC\'s gate table when the run is sent', async () => {
    let screenSeenMidRun = '';
    const kit = makeKit({
      photos: 2,
      onRead: () => {
        screenSeenMidRun ||= JSON.stringify(screen.toJSON());
      },
    });
    await renderWithProviders(<EvalsScreen runner={kit.runner} />);
    await act(async () => {
      fireEvent.press(screen.getByRole('button', { name: 'Start the run' }));
    });

    expect(await screen.findByText(/M3 gate: pass/)).toBeTruthy();
    expect(screenSeenMidRun).toMatch(/Photo 1 of 2/);
    expect(screenSeenMidRun).toMatch(/Apple model/);
    expect(screen.getByText(/shelf_recall: 93%/)).toBeTruthy();
    expect(screen.getByText('Sent 2 photos to your PC.')).toBeTruthy();
  });

  it('tells the owner to keep the phone cool when it gets hot', async () => {
    const kit = makeKit({ photos: 1 });
    jest.spyOn(kit.vision, 'deviceStatus').mockResolvedValue({ thermalState: 'serious', osVersion: '26.0' });
    let hot = false;
    kit.runner.subscribe(() => {
      hot ||= kit.runner.getState().keepCool;
    });
    await renderWithProviders(<EvalsScreen runner={kit.runner} />);
    await act(async () => {
      await kit.runner.start();
    });
    expect(hot).toBe(true);
  });

  it('offers to send the results again when the upload fails', async () => {
    const kit = makeKit({ photos: 1 });
    evalsMock.failNextSubmit = 500;
    await renderWithProviders(<EvalsScreen runner={kit.runner} />);
    await act(async () => {
      fireEvent.press(screen.getByRole('button', { name: 'Start the run' }));
    });
    const retry = await screen.findByRole('button', { name: 'Send 2 results again' });
    await act(async () => {
      fireEvent.press(retry);
    });
    expect(await screen.findByText(/M3 gate: pass/)).toBeTruthy();
  });
});
