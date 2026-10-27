// Wiring (#442): focusing a field inside a keyboard-avoiding Screen scrolls it
// above the keyboard, and showing the keyboard re-scrolls to the last focused
// field (focus fires before the keyboard frame exists).
import { fireEvent, render, screen } from '@testing-library/react-native';
import { DeviceEventEmitter, Platform } from 'react-native';
import { Screen } from './Screen';
import { TextField } from './TextField';
import { scrollInputAboveKeyboard } from './keyboardScroll';

jest.mock('./keyboardScroll', () => {
  const actual = jest.requireActual('./keyboardScroll');
  return { ...actual, scrollInputAboveKeyboard: jest.fn() };
});
const scrolled = scrollInputAboveKeyboard as jest.Mock;

const original = Platform.OS;
beforeEach(() => scrolled.mockClear());
afterEach(() => {
  Platform.OS = original;
});

describe('Screen scroll to the focused field', () => {
  it('scrolls the focused field above the keyboard', async () => {
    Platform.OS = 'ios';
    await render(
      <Screen subtitle="Edit" keyboardAvoiding>
        <TextField label="Notes" value="" onChangeText={() => {}} />
      </Screen>,
    );

    fireEvent(screen.getByLabelText('Notes'), 'focus');

    expect(scrolled).toHaveBeenCalledTimes(1);
  });

  it('re-scrolls to the last focused field when the keyboard appears', async () => {
    Platform.OS = 'ios';
    await render(
      <Screen subtitle="Edit" keyboardAvoiding>
        <TextField label="Notes" value="" onChangeText={() => {}} />
      </Screen>,
    );
    fireEvent(screen.getByLabelText('Notes'), 'focus');
    scrolled.mockClear();

    DeviceEventEmitter.emit('keyboardDidShow', { endCoordinates: { height: 300 } });

    expect(scrolled).toHaveBeenCalledTimes(1);
  });

  it('leaves fields alone on screens without keyboard avoidance', async () => {
    Platform.OS = 'ios';
    await render(
      <Screen subtitle="Library">
        <TextField label="Notes" value="" onChangeText={() => {}} />
      </Screen>,
    );

    fireEvent(screen.getByLabelText('Notes'), 'focus');

    expect(scrolled).not.toHaveBeenCalled();
  });
});
