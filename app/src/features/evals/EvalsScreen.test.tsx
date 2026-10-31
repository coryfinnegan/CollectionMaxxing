import { act, fireEvent, screen } from '@testing-library/react-native';
import { View, type ViewProps } from 'react-native';
import { setPictureFiles } from '@/features/scan/pictureFiles';
import { configureAuth, resetAuth } from '@/lib/auth/session';
import { MemoryNativeAuth } from '@/lib/auth/native';
import { evalsMock } from '@/mocks/evals';
import { renderWithProviders } from '@/test/render';
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

const press = async (name: string | RegExp) => {
  await act(async () => {
    fireEvent.press(await screen.findByRole('button', { name }));
  });
};

describe('EvalsScreen', () => {
  it('keeps the content clear of the notch and home indicator', async () => {
    const kit = makeKit({ photos: 1 });
    await renderWithProviders(<EvalsScreen runner={kit.runner} />);
    expect(screen.getByTestId('safe-area')).toBeTruthy();
  });

  it('tells the owner each photo goes up as soon as it is read, so they can leave and pick up where they left off', async () => {
    const kit = makeKit({ photos: 1 });
    await renderWithProviders(<EvalsScreen runner={kit.runner} />);
    expect(screen.getByText(/Each photo goes to your PC as soon as it's read, so you can leave and pick up where you left off\./)).toBeTruthy();
  });

  it('asks the PC for a run in progress when it opens, then offers to start one when there is none', async () => {
    const kit = makeKit({ photos: 1 });
    await renderWithProviders(<EvalsScreen runner={kit.runner} />);
    expect(await screen.findByRole('button', { name: 'Start the run' })).toBeTruthy();
    expect(evalsMock.asked).toBe(1);
  });

  it('shows the progress, then the PC\'s gate table when the PC has scored the run', async () => {
    let screenSeenMidRun = '';
    const kit = makeKit({
      photos: 2,
      onRead: () => {
        screenSeenMidRun ||= JSON.stringify(screen.toJSON());
      },
    });
    await renderWithProviders(<EvalsScreen runner={kit.runner} />);
    await press('Start the run');

    expect(await screen.findByText(/M3 gate: pass/)).toBeTruthy();
    expect(screenSeenMidRun).toMatch(/Photo 1 of 2/);
    expect(screenSeenMidRun).toMatch(/Apple model/);
    expect(screen.getByText(/shelf_recall: 93%/)).toBeTruthy();
    expect(screen.getByText('Your PC scored 2 photos.')).toBeTruthy();
  });

  it('says the PC is scoring and that the owner can leave, until the summary is ready', async () => {
    evalsMock.scoredAfterPolls = 1000;
    const kit = makeKit({ photos: 1, onSleep: () => new Promise<void>(() => undefined) });
    await renderWithProviders(<EvalsScreen runner={kit.runner} />);
    await press('Start the run');

    expect(await screen.findByText('Your PC is scoring the run.')).toBeTruthy();
    expect(screen.getByText(/You can leave this screen\. The gate summary shows here when it's ready\./)).toBeTruthy();
    expect(screen.queryByText(/M3 gate/)).toBeNull();
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
      fireEvent.changeText(await screen.findByLabelText('Baseline note'), 'T1 bar moved for the re-run.');
    });
    await press('Start the run');
    expect(await screen.findByText(/M3 gate: pass/)).toBeTruthy();
    const opened = evalsMock.opened as { baselineNote?: string }[];
    expect(opened).toHaveLength(1);
    expect(opened[0].baselineNote).toBe('T1 bar moved for the re-run.');
  });

  describe('a run the PC is holding', () => {
    it('shows how many photos are read with Continue and Start over, and reads only the rest', async () => {
      const kit = makeKit({ photos: 4, held: ['photo-001', 'photo-003'] });
      await renderWithProviders(<EvalsScreen runner={kit.runner} />);

      expect(await screen.findByText('2 of 4 photos read')).toBeTruthy();
      expect(screen.getByRole('button', { name: 'Start over' })).toBeTruthy();
      expect(screen.queryByRole('button', { name: 'Start the run' })).toBeNull();
      await press('Continue');

      expect(await screen.findByText(/M3 gate: pass/)).toBeTruthy();
      expect(kit.downloaded).toEqual(['photo-002', 'photo-004']);
    });

    it('asks once before Start over throws the run away, and Keep the run backs out', async () => {
      const kit = makeKit({ photos: 3, held: ['photo-001'] });
      await renderWithProviders(<EvalsScreen runner={kit.runner} />);
      await press('Start over');

      expect(await screen.findByText(/Your PC throws away this run and the 1 photo it has read\./)).toBeTruthy();
      expect(evalsMock.discards).toBe(0);
      await press('Keep the run');
      expect(evalsMock.discards).toBe(0);
      expect(screen.getByRole('button', { name: 'Continue' })).toBeTruthy();

      await press('Start over');
      await press('Throw away 1 photo and start over');

      expect(await screen.findByRole('button', { name: 'Start the run' })).toBeTruthy();
      expect(evalsMock.discards).toBe(1);
      expect(evalsMock.run).toBeUndefined();
    });

    it('does not ask before throwing away a run with nothing read in it', async () => {
      const kit = makeKit({ photos: 3, held: [] });
      await renderWithProviders(<EvalsScreen runner={kit.runner} />);
      expect(await screen.findByText('0 of 3 photos read')).toBeTruthy();

      await press('Start over');

      expect(await screen.findByRole('button', { name: 'Start the run' })).toBeTruthy();
      expect(evalsMock.discards).toBe(1);
      expect(screen.queryByText(/Start over\?/)).toBeNull();
    });

    it('says why the PC cannot score the run, that it keeps trying, and offers Start over', async () => {
      const kit = makeKit({ photos: 2, held: ['photo-001', 'photo-002'], onSleep: () => new Promise<void>(() => undefined) });
      evalsMock.run!.state = 'finishing';
      evalsMock.run!.failure = 'evals.results_not_writable';
      evalsMock.scoredAfterPolls = 1000;
      await renderWithProviders(<EvalsScreen runner={kit.runner} />);

      expect(await screen.findByText("Your PC couldn't score the run: its results folder can't be written. It keeps trying.")).toBeTruthy();
      expect(screen.queryByText('Your PC is scoring the run.')).toBeNull();
      await press('Start over');
      await press('Throw away 2 photos and start over');

      expect(await screen.findByRole('button', { name: 'Start the run' })).toBeTruthy();
      expect(evalsMock.discards).toBe(1);
    });

    it('shows the gate summary of a run the PC finished while the owner was away', async () => {
      const kit = makeKit({ photos: 2, held: ['photo-001', 'photo-002'] });
      evalsMock.run!.state = 'done';
      await renderWithProviders(<EvalsScreen runner={kit.runner} />);

      expect(await screen.findByText(/M3 gate: pass/)).toBeTruthy();
      expect(screen.getByRole('button', { name: 'Run again' })).toBeTruthy();
    });
  });

  describe('while it runs', () => {
    it('stops with a button that says how many photos the PC will score', async () => {
      let stopped = false;
      const kit: ReturnType<typeof makeKit> = makeKit({
        photos: 4,
        onRead: async () => {
          if (kit.downloaded.length === 2 && !stopped) {
            stopped = true;
            await act(async () => {
              fireEvent.press(await screen.findByRole('button', { name: 'Stop and score the 1 read so far' }));
            });
          }
        },
      });
      await renderWithProviders(<EvalsScreen runner={kit.runner} />);
      await press('Start the run');

      expect(await screen.findByText(/M3 gate: incomplete/)).toBeTruthy();
      expect(evalsMock.finishes).toEqual([{ partial: true }]);
      expect(screen.getByText('Stopped early. Your PC scored 1 photo.')).toBeTruthy();
    });

    it('says it is waiting for the PC when a photo does not go up, and that the readings are kept on the phone', async () => {
      let seen = '';
      const kit = makeKit({
        photos: 1,
        onSleep: async () => {
          await act(async () => undefined); // let the screen show the state the runner just set
          seen ||= JSON.stringify(screen.toJSON());
        },
      });
      evalsMock.failSends = ['network'];
      await renderWithProviders(<EvalsScreen runner={kit.runner} />);
      await press('Start the run');

      expect(await screen.findByText(/M3 gate: pass/)).toBeTruthy();
      expect(seen).toMatch(/Waiting for your PC\. The 1 photo read so far is kept on this phone and go/);
    });

    it('says a pause because the app went to the background lifts by itself', async () => {
      let left = false;
      const kit: ReturnType<typeof makeKit> = makeKit({
        photos: 1,
        onRead: () => {
          if (!left) {
            left = true;
            kit.app.go('background');
          }
        },
      });
      await renderWithProviders(<EvalsScreen runner={kit.runner} />);
      await press('Start the run');

      expect(await screen.findByText(/Paused while the app is in the background\. It picks up on its own when you come back\./)).toBeTruthy();
      await act(async () => {
        kit.app.go('active');
      });
      expect(await screen.findByText(/M3 gate: pass/)).toBeTruthy();
    });

    it('pauses and resumes at the owner\'s word', async () => {
      let paused = false;
      const kit: ReturnType<typeof makeKit> = makeKit({
        photos: 2,
        onRead: () => {
          if (!paused) {
            paused = true;
            kit.runner.pause();
          }
        },
      });
      await renderWithProviders(<EvalsScreen runner={kit.runner} />);
      await press('Start the run');

      expect(await screen.findByText('Paused.')).toBeTruthy();
      await press('Resume');
      expect(await screen.findByText(/M3 gate: pass/)).toBeTruthy();
    });
  });

  it('shows the shared loading state while it checks the PC', async () => {
    const kit = makeKit({ photos: 1 });
    expect(kit.runner.getState().phase).toBe('checking');
    jest.spyOn(kit.runner, 'refresh').mockReturnValue(new Promise<never>(() => undefined));
    await renderWithProviders(<EvalsScreen runner={kit.runner} />);
    expect(await screen.findByTestId('loading-state')).toBeTruthy();
    expect(screen.getByText('Checking your PC for a run in progress.')).toBeTruthy();
  });

  it('says the PC could not take what was read, keeps it, and offers Check again and then Continue', async () => {
    const kit = makeKit({ photos: 2 });
    evalsMock.failSends = [{ status: 409, code: 'evals.results_not_writable' }];
    await renderWithProviders(<EvalsScreen runner={kit.runner} />);
    await press('Start the run');

    expect(await screen.findByText("Couldn't send to your PC. What's read is kept on this phone; check again to pick up.")).toBeTruthy();
    await press('Check again');
    expect(await screen.findByText('1 of 2 photos read')).toBeTruthy();
    expect(screen.getByText(/1 photo read on this phone go up first\./)).toBeTruthy();
    await press('Continue');

    expect(await screen.findByText(/M3 gate: pass/)).toBeTruthy();
    expect(evalsMock.sent.map((s) => s.photoId)).toEqual(['photo-001', 'photo-002']);
    expect(kit.downloaded).toEqual(['photo-001', 'photo-002']);
  });

  it('offers to look again when the run could not be opened', async () => {
    const kit = makeKit({ photos: 1 });
    await renderWithProviders(<EvalsScreen runner={kit.runner} />);
    evalsMock.photos = [];
    await press('Start the run');

    expect(await screen.findByText("Couldn't open a run on your PC.")).toBeTruthy();
    await press('Check again');
    expect(await screen.findByRole('button', { name: 'Start the run' })).toBeTruthy();
  });
});
