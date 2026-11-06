import { fireEvent, render, screen, waitFor } from '@testing-library/react-native';
import { Linking, View, type ViewProps } from 'react-native';
import { AboutScreen } from './AboutScreen';
import { ATTRIBUTIONS } from './attributions';
import * as buildInfo from './buildInfo';
import { PRIVACY_NOTE } from './privacyNote';

jest.mock('expo-constants', () => ({
  __esModule: true,
  default: { expoConfig: { version: '1.2.3', ios: { buildNumber: '45' } } },
}));

const mockDeviceStatus = jest.fn();
jest.mock('@/native/cm-vision', () => ({ visionEngine: { deviceStatus: () => mockDeviceStatus() } }));

const mockSafeAreaView = ({ children }: ViewProps) => <View testID="safe-area">{children}</View>;
jest.mock('react-native-safe-area-context', () => ({
  SafeAreaView: (props: ViewProps) => mockSafeAreaView(props),
}));

describe('About', () => {
  const reports = (buildNumber?: string) =>
    mockDeviceStatus.mockResolvedValue({ thermalState: 'nominal', osVersion: '26.0', buildNumber });

  it('About shows the build number the device reports', async () => {
    reports('150');
    await render(<AboutScreen />);
    await waitFor(() => expect(screen.getByText('Version 1.2.3 (build 150)')).toBeTruthy());
  });

  it('About shows no build when the device reports none', async () => {
    reports('');
    await render(<AboutScreen />);
    await waitFor(() => expect(screen.getByText('Version 1.2.3')).toBeTruthy());
    expect(screen.queryByText(/\(build/)).toBeNull();
  });

  it('About ignores the expo config build number', async () => {
    mockDeviceStatus.mockRejectedValue(new Error('no native module'));
    await render(<AboutScreen />);
    expect(screen.getByText('Version 1.2.3')).toBeTruthy();
    expect(screen.queryByText(/45/)).toBeNull();
  });

  it('shows which build is running, so a deploy can be confirmed from the phone', async () => {
    jest.spyOn(buildInfo, 'buildLine').mockReturnValue('Build 559f42c from Oct 19, 2026');
    await render(<AboutScreen />);
    expect(screen.getByText('Build 559f42c from Oct 19, 2026')).toBeTruthy();
  });

  it('shows the privacy note', async () => {
    await render(<AboutScreen />);
    for (const paragraph of PRIVACY_NOTE) expect(screen.getByText(paragraph)).toBeTruthy();
  });

  it('keeps the content clear of the notch and home indicator', async () => {
    await render(<AboutScreen />);
    expect(screen.getByTestId('safe-area')).toBeTruthy();
  });

  it('shows one credit per provider as a labelled link that opens in the system browser', async () => {
    const open = jest.spyOn(Linking, 'openURL').mockResolvedValue(true);
    await render(<AboutScreen />);
    expect(screen.getByText('Provider data')).toBeTruthy();
    for (const a of ATTRIBUTIONS) {
      expect(screen.getByText(a.notice)).toBeTruthy();
      await fireEvent.press(screen.getByRole('link', { name: `${a.notice} ${a.url}` }));
      expect(open).toHaveBeenCalledWith(a.url);
    }
  });

  it('names each credit link starting with the words it shows, then the address (WCAG 2.5.3)', async () => {
    await render(<AboutScreen />);
    for (const a of ATTRIBUTIONS) {
      const label = screen.getByRole('link', { name: `${a.notice} ${a.url}` }).props.accessibilityLabel as string;
      expect(label.startsWith(a.notice)).toBe(true);
      expect(label).toContain(a.url);
    }
  });
});
