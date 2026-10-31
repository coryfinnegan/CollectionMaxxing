// One title per screen (#302): on the phone the navigator header owns the section title,
// so Screen shows its title only on the web. Content titles (an item's name) and screens
// with no navigator header opt back in with showTitleOnPhone. The subtitle is content
// and always shows.
import { render, screen } from '@testing-library/react-native';
import { Platform, Text as RNText } from 'react-native';
import { Screen } from './Screen';

const original = Platform.OS;
afterEach(() => {
  Platform.OS = original;
});

describe('Screen titles', () => {
  it('shows the section title on the web', async () => {
    Platform.OS = 'web';
    await render(
      <Screen title="Library" subtitle="Everything you own.">
        <RNText>Body</RNText>
      </Screen>,
    );
    expect(screen.getByRole('header', { name: 'Library' })).toBeTruthy();
    expect(screen.getByText('Everything you own.')).toBeTruthy();
  });

  it('hides the section title on the phone, where the navigator header names the screen', async () => {
    Platform.OS = 'ios';
    await render(
      <Screen title="Library" subtitle="Everything you own.">
        <RNText>Body</RNText>
      </Screen>,
    );
    expect(screen.queryByRole('header', { name: 'Library' })).toBeNull();
    expect(screen.queryByText('Library')).toBeNull();
    expect(screen.getByText('Everything you own.')).toBeTruthy();
    expect(screen.getByText('Body')).toBeTruthy();
  });

  it('shows a content title on the phone when asked', async () => {
    Platform.OS = 'ios';
    await render(
      <Screen title="The Matrix" subtitle="DVD · 1999" showTitleOnPhone>
        <RNText>Body</RNText>
      </Screen>,
    );
    expect(screen.getByRole('header', { name: 'The Matrix' })).toBeTruthy();
    expect(screen.getByText('DVD · 1999')).toBeTruthy();
  });
});

describe('Screen keyboard avoidance', () => {
  it('wraps the form in a KeyboardAvoidingView when asked (#442)', async () => {
    Platform.OS = 'ios';
    await render(
      <Screen subtitle="Edit" keyboardAvoiding>
        <RNText>Body</RNText>
      </Screen>,
    );
    expect(screen.getByTestId('keyboard-avoiding-view')).toBeTruthy();
  });

  it('lets taps reach controls while the keyboard is up (#442)', async () => {
    Platform.OS = 'ios';
    await render(
      <Screen subtitle="Edit" keyboardAvoiding>
        <RNText>Body</RNText>
      </Screen>,
    );
    expect(screen.getByTestId('keyboard-avoiding-scroll').props.keyboardShouldPersistTaps).toBe('handled');
  });

  it('does not wrap other screens in a KeyboardAvoidingView by default', async () => {
    Platform.OS = 'ios';
    await render(
      <Screen subtitle="Library">
        <RNText>Body</RNText>
      </Screen>,
    );
    expect(screen.queryByTestId('keyboard-avoiding-view')).toBeNull();
  });

  it('lets the first tap reach a control while the keyboard is up, on every screen', async () => {
    Platform.OS = 'ios';
    await render(
      <Screen subtitle="Library">
        <RNText>Body</RNText>
      </Screen>,
    );
    expect(screen.getByTestId('screen-scroll').props.keyboardShouldPersistTaps).toBe('handled');
  });
});
