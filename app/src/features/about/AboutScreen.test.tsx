import { fireEvent, render, screen } from '@testing-library/react-native';
import { Linking } from 'react-native';
import { AboutScreen } from './AboutScreen';
import { ATTRIBUTIONS } from './attributions';
import { PRIVACY_NOTE } from './privacyNote';

jest.mock('expo-constants', () => ({
  __esModule: true,
  default: { expoConfig: { version: '1.2.3', ios: { buildNumber: '45' } } },
}));

describe('About', () => {
  it('shows the app version and build', async () => {
    await render(<AboutScreen />);
    expect(screen.getByText('Version 1.2.3 (build 45)')).toBeTruthy();
  });

  it('shows the privacy note', async () => {
    await render(<AboutScreen />);
    for (const paragraph of PRIVACY_NOTE) expect(screen.getByText(paragraph)).toBeTruthy();
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
