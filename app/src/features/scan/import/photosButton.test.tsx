// The Photos button is a phone thing: not on the web build, and only where the picture pipeline runs.
import { Platform } from 'react-native';
import { screen } from '@testing-library/react-native';
import { MOCK_LIBRARY_ID } from '@/mocks/data';
import { renderWithProviders } from '@/test/render';
import { ScannerScreen } from '../scanner/ScannerScreen';
import { FakeScannerSource } from '../scanner/ScannerSource';
import { makeKit, resetKit } from '../pipeline/testKit';
import { importKit } from './importKit';

jest.mock('expo-router', () => ({ useRouter: () => ({ push: jest.fn(), back: jest.fn(), replace: jest.fn() }) }));

const original = Platform.OS;
afterEach(() => {
  Platform.OS = original;
  resetKit();
});

describe('the Photos button', () => {
  it('shows on the phone', async () => {
    const kit = makeKit();
    await importKit(kit).open();
    expect(screen.getByLabelText('Photos')).toBeTruthy();
  });

  it('is hidden on web', async () => {
    Platform.OS = 'web';
    const kit = makeKit();
    await importKit(kit).open();
    expect(screen.queryByLabelText('Photos')).toBeNull();
  });

  it('is hidden when there is no picture pipeline', async () => {
    await renderWithProviders(<ScannerScreen libraryId={MOCK_LIBRARY_ID} source={new FakeScannerSource()} />);
    expect(screen.queryByLabelText('Photos')).toBeNull();
  });
});
