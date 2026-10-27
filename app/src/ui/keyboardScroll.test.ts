// Scroll-to-focused-input (#442): the form scrolls the focused field above the
// keyboard through the scroll view's own responder, which already tracks the
// real keyboard frame. Never throws: measuring can fail on unmounted inputs.
import { Platform, type ScrollView, type TextInput } from 'react-native';
import { keyboardAvoidingBehavior, scrollInputAboveKeyboard } from './keyboardScroll';

// The real module object, so the spy is visible to the module under test
// (a namespace import would be a copy Babel made).
const RN = jest.requireActual<typeof import('react-native')>('react-native');

const original = Platform.OS;
afterEach(() => {
  Platform.OS = original;
  jest.restoreAllMocks();
});

function responder() {
  const scrollResponderScrollNativeHandleToKeyboard = jest.fn();
  const scrollView = { getScrollResponder: () => ({ scrollResponderScrollNativeHandleToKeyboard }) };
  return { scrollView: scrollView as unknown as ScrollView, scrollResponderScrollNativeHandleToKeyboard };
}

describe('scrollInputAboveKeyboard', () => {
  it('scrolls the focused input above the keyboard', () => {
    Platform.OS = 'ios';
    const measured = jest.spyOn(RN, 'findNodeHandle').mockReturnValue(7);
    const { scrollView, scrollResponderScrollNativeHandleToKeyboard } = responder();

    scrollInputAboveKeyboard(scrollView, {} as TextInput);

    expect(measured).toHaveBeenCalled();
    expect(scrollResponderScrollNativeHandleToKeyboard).toHaveBeenCalledWith(7, expect.any(Number), true);
  });

  it('does nothing on the web, where the browser scrolls focused inputs itself', () => {
    Platform.OS = 'web';
    const measured = jest.spyOn(RN, 'findNodeHandle');
    const { scrollView, scrollResponderScrollNativeHandleToKeyboard } = responder();

    scrollInputAboveKeyboard(scrollView, {} as TextInput);

    expect(measured).not.toHaveBeenCalled();
    expect(scrollResponderScrollNativeHandleToKeyboard).not.toHaveBeenCalled();
  });

  it('stays quiet when there is nothing to scroll', () => {
    Platform.OS = 'ios';
    const measured = jest.spyOn(RN, 'findNodeHandle');
    const { scrollView, scrollResponderScrollNativeHandleToKeyboard } = responder();

    expect(() => scrollInputAboveKeyboard(null, {} as TextInput)).not.toThrow();
    expect(() => scrollInputAboveKeyboard(scrollView, null)).not.toThrow();
    expect(measured).not.toHaveBeenCalled();
    expect(scrollResponderScrollNativeHandleToKeyboard).not.toHaveBeenCalled();
  });

  it('stays quiet when measuring fails', () => {
    Platform.OS = 'ios';
    jest.spyOn(RN, 'findNodeHandle').mockImplementation(() => {
      throw new Error('unmounted');
    });
    const { scrollView, scrollResponderScrollNativeHandleToKeyboard } = responder();

    expect(() => scrollInputAboveKeyboard(scrollView, {} as TextInput)).not.toThrow();
    expect(scrollResponderScrollNativeHandleToKeyboard).not.toHaveBeenCalled();
  });
});

describe('keyboardAvoidingBehavior', () => {
  it('pads around the keyboard on iOS', () => {
    Platform.OS = 'ios';
    expect(keyboardAvoidingBehavior()).toBe('padding');
  });

  it('shrinks the view elsewhere', () => {
    Platform.OS = 'android';
    expect(keyboardAvoidingBehavior()).toBe('height');
    Platform.OS = 'web';
    expect(keyboardAvoidingBehavior()).toBe('height');
  });
});
