// One accent per item, resolved from its cover art and cached (#306). Detail screens only.
import { screen, waitFor } from '@testing-library/react-native';
import { Text } from '@/ui';
import { renderWithProviders } from '@/test/render';
import { clearCoverAccents, useCoverAccent } from './useCoverAccent';

jest.mock('react-native-image-colors', () => ({ getColors: (...args: unknown[]) => mockGetColors(...args) }));

const mockGetColors = jest.fn();

function Probe({ id, uri }: { id: string; uri: string | null }) {
  const accent = useCoverAccent(id, uri);
  return <Text>{accent ?? 'none'}</Text>;
}

const webPalette = {
  dominant: '#111111',
  vibrant: '#222222',
  darkVibrant: '#333333',
  lightVibrant: '#444444',
  darkMuted: '#555555',
  lightMuted: '#666666',
  muted: '#777777',
  platform: 'web',
} as const;

beforeEach(() => {
  clearCoverAccents();
  mockGetColors.mockReset();
  mockGetColors.mockResolvedValue(webPalette);
});

describe('useCoverAccent', () => {
  it('stays silent when there is no cover to read', async () => {
    await renderWithProviders(<Probe id="item-1" uri={null} />);

    expect(await screen.findByText('none')).toBeTruthy();
    expect(mockGetColors).not.toHaveBeenCalled();
  });

  it('resolves the muted tone once per item and caches failures too', async () => {
    const first = await renderWithProviders(<Probe id="item-1" uri="https://covers.test/matrix.jpg" />);
    expect(await screen.findByText('#777777')).toBeTruthy();
    expect(mockGetColors).toHaveBeenCalledTimes(1);
    expect(mockGetColors).toHaveBeenCalledWith('https://covers.test/matrix.jpg', { cache: true, key: 'item-1' });
    await first.unmount();

    await renderWithProviders(<Probe id="item-1" uri="https://covers.test/matrix.jpg" />);
    expect(await screen.findByText('#777777')).toBeTruthy();
    expect(mockGetColors).toHaveBeenCalledTimes(1);

    mockGetColors.mockRejectedValueOnce(new Error('offline'));
    const failing = await renderWithProviders(<Probe id="item-2" uri="https://covers.test/gone.jpg" />);
    await waitFor(() => expect(mockGetColors).toHaveBeenCalledTimes(2));
    expect(screen.getByText('none')).toBeTruthy();
    await failing.unmount();

    await renderWithProviders(<Probe id="item-2" uri="https://covers.test/gone.jpg" />);
    await waitFor(() => expect(screen.getByText('none')).toBeTruthy());
    expect(mockGetColors).toHaveBeenCalledTimes(2);
  });

  it('ignores a late answer after the cover changes', async () => {
    let release!: (value: unknown) => void;
    mockGetColors.mockReturnValueOnce(new Promise((resolve) => void (release = resolve)));
    const view = await renderWithProviders(<Probe id="item-1" uri="https://covers.test/old.jpg" />);
    expect(mockGetColors).toHaveBeenCalledTimes(1);

    await view.rerender(<Probe id="item-1" uri="https://covers.test/new.jpg" />);
    release({ ...webPalette, muted: '#0a0a0a' });
    expect(await screen.findByText('#777777')).toBeTruthy();
  });
});
