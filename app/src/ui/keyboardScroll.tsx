// Scroll-to-focused-input (#442): a keyboard-avoiding Screen scrolls the
// focused field above the keyboard through the scroll view's own responder,
// which already tracks the real keyboard frame. TextField reports focus
// through the scope below; outside an avoiding Screen the scope is absent
// and fields behave exactly as before.
import { createContext, useContext, type ReactNode } from 'react';
import { findNodeHandle, Platform, type ScrollView, type TextInput } from 'react-native';

/** Breathing room kept between the focused field and the keyboard. */
export const FOCUSED_INPUT_OFFSET = 80;

/** Padding resizes around the iOS keyboard; height shrinks the view elsewhere. */
export function keyboardAvoidingBehavior(): 'padding' | 'height' {
  return Platform.OS === 'ios' ? 'padding' : 'height';
}

type InputFocusReporter = (input: TextInput | null) => void;

const InputFocusContext = createContext<InputFocusReporter | null>(null);

export function useReportInputFocus(): InputFocusReporter | null {
  return useContext(InputFocusContext);
}

export function InputFocusScope({ onFocus, children }: { onFocus: InputFocusReporter; children: ReactNode }) {
  return <InputFocusContext.Provider value={onFocus}>{children}</InputFocusContext.Provider>;
}

type KeyboardScrollingResponder = {
  scrollResponderScrollNativeHandleToKeyboard?: (
    nodeHandle: number,
    additionalOffset: number,
    preventNegativeScrollOffset: boolean,
  ) => void;
};

/** Scrolls the focused input above the keyboard. Never throws. */
export function scrollInputAboveKeyboard(scrollView: ScrollView | null, input: TextInput | null): void {
  // The browser scrolls focused inputs into view on its own.
  if (Platform.OS === 'web') return;
  if (!scrollView || !input) return;
  try {
    const handle = findNodeHandle(input);
    if (handle == null) return;
    const responder = scrollView.getScrollResponder() as unknown as KeyboardScrollingResponder | null;
    responder?.scrollResponderScrollNativeHandleToKeyboard?.(handle, FOCUSED_INPUT_OFFSET, true);
  } catch {
    // Measuring can fail on unmounted inputs; the keyboard still opened, so stay quiet.
  }
}
