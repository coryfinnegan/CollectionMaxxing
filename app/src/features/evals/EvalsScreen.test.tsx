import { act, fireEvent, screen } from '@testing-library/react-native';
import { View, type ViewProps } from 'react-native';
import { setPictureFiles } from '@/features/scan/pictureFiles';
import { configureAuth, resetAuth } from '@/lib/auth/session';
import { MemoryNativeAuth } from '@/lib/auth/native';
import { evalsMock } from '@/mocks/evals';
import { renderWithProviders } from '@/test/render';
import { evalApi } from './api';
import { EvalsScreen } from './EvalsScreen';
import { makeKit } from './evalsTestKit';

const mockSafeAreaView = ({ children }: ViewProps) => <View testID="safe-area">{children}</View>;
jest.mock('react-native-safe-area-context', () => ({
  SafeAreaView: (props: ViewProps) => mockSafeAreaView(props),
  SafeAreaProvider: ({ children }: ViewProps) => <>{children}</>,
  useSafeAreaInsets: () => ({ top: 0, bottom: 0, left: 0, right: 0 }),
}));

beforeEach(() => {
  evalsMock.reset();
  configureAuth({ platform: 'native', native: new MemoryNativeAuth() });
});
afterEach(() => {
  resetAuth();
  setPictureFiles(undefined);
});

describe('EvalsScreen', () => {
  it('keeps the content clear of the notch and home indicator', async () => {
    const kit = makeKit({ photos: 1 });
    await renderWithProviders(<EvalsScreen runner={kit.runner} />);
    expect(screen.getByTestId('safe-area')).toBeTruthy();
  });

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

  it('sends the baseline note typed before the run, and none when left empty', async () => {
    const kit = makeKit({ photos: 1 });
    await renderWithProviders(<EvalsScreen runner={kit.runner} />);
    await act(async () => {
      fireEvent.changeText(screen.getByLabelText('Baseline note'), 'T1 bar moved for the re-run.');
    });
    await act(async () => {
      fireEvent.press(screen.getByRole('button', { name: 'Start the run' }));
    });
    expect(await screen.findByText(/M3 gate: pass/)).toBeTruthy();
    const runs = evalsMock.runs as { baselineNote?: string }[];
    expect(runs).toHaveLength(1);
    expect(runs[0].baselineNote).toBe('T1 bar moved for the re-run.');
  });

  it('offers to send the results again when the upload fails', async () => {
    const kit = makeKit({ photos: 1 });
    evalsMock.failNextSubmit = 500;
    await renderWithProviders(<EvalsScreen runner={kit.runner} />);
    await act(async () => {
      fireEvent.press(screen.getByRole('button', { name: 'Start the run' }));
    });
    const retry = await screen.findByRole('button', { name: 'Send 3 results again' });
    await act(async () => {
      fireEvent.press(retry);
    });
    expect(await screen.findByText(/M3 gate: pass/)).toBeTruthy();
  });

  it('shows the shared loading state while the results go up to the PC', async () => {
    const kit = makeKit({ photos: 1 });
    // Hold the upload open so the uploading branch stays on screen.
    const submit = jest.spyOn(evalApi, 'submit').mockReturnValue(new Promise<never>(() => {}));
    try {
      await renderWithProviders(<EvalsScreen runner={kit.runner} />);
      await act(async () => {
        fireEvent.press(screen.getByRole('button', { name: 'Start the run' }));
      });
      expect(await screen.findByTestId('loading-state')).toBeTruthy();
      expect(screen.getByText('Sending the results to your PC.')).toBeTruthy();
    } finally {
      submit.mockRestore();
    }
  });
});
