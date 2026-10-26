// The appearance choice keeps working where the native store is missing
// (Storybook renders through the MMKV stub): the choice lives in memory.
import { setAppearanceStorage, useAppearanceChoice } from './appearance';

jest.mock('react-native-mmkv', () => {
  throw new Error('native module is missing');
});

describe('appearance without the native store', () => {
  beforeEach(() => {
    setAppearanceStorage(undefined);
    useAppearanceChoice.setState({ choice: 'system' });
  });

  it('follows the system until the person picks', () => {
    useAppearanceChoice.getState().reload();
    expect(useAppearanceChoice.getState().choice).toBe('system');
  });

  it('keeps the choice in memory across reloads', () => {
    useAppearanceChoice.getState().set('dark');
    useAppearanceChoice.setState({ choice: 'system' });
    useAppearanceChoice.getState().reload();
    expect(useAppearanceChoice.getState().choice).toBe('dark');
  });
});
