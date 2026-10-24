import { fireEvent, render, screen } from '@testing-library/react-native';
import { Linking, View, type ViewProps } from 'react-native';
import { AboutScreen } from './AboutScreen';
import { ATTRIBUTIONS } from './attributions';
import * as buildInfo from './buildInfo';
import { PRIVACY_NOTE } from './privacyNote';

jest.mock('expo-constants', () => ({
  __esModule: true,
  default: { expoConfig: { version: '1.2.3', ios: { buildNumber: '45' } } },
}));

const mockRouter = { push: jest.fn(), back: jest.fn(), replace: jest.fn() };
jest.mock('expo-router', () => ({ useRouter: () => mockRouter }));

const mockSafeAreaView = ({ children }: ViewProps) => <View testID="safe-area">{children}</View>;
jest.mock('react-native-safe-area-context', () => ({
  SafeAreaView: (props: ViewProps) => mockSafeAreaView(props),
}));

describe('About', () => {
  it('shows the app version and build', async () => {
    await render(<AboutScreen />);
    expect(screen.getByText('Version 1.2.3 (build 45)')).toBeTruthy();
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

  it('shows a back button that returns to the previous screen', async () => {
    mockRouter.back.mockClear();
    await render(<AboutScreen />);
    fireEvent.press(screen.getByRole('button', { name: 'Back' }));
    expect(mockRouter.back).toHaveBeenCalledTimes(1);
  });

  it('shows one credit per provider as a labelled link that opens in the system browser', async () => {
    const open = jest.spyOn(Linking, 'openURL').mockResolvedValue(true);
    await render(<AboutScreen />);
    expect(screen.getByText('Provider data')).toBeTruthy();
    for (const a of ATTRIBUTIONS) {
      expect(screen.getByText(a.notice)).toBeTruthy();
      fireEvent.press(screen.getByRole('link', { name: `${a.name}: ${a.url}` }));
      expect(open).toHaveBeenCalledWith(a.url);
    }
  });
});
