// First: this module needs Unistyles configured before it runs (#305).
import '@/theme/unistyles';
import type { ReactNode } from 'react';
import { useCallback, useEffect, useRef } from 'react';
import { Keyboard, KeyboardAvoidingView, Platform, ScrollView, type TextInput, View } from 'react-native';
import { StyleSheet } from 'react-native-unistyles';
import { InputFocusScope, keyboardAvoidingBehavior, scrollInputAboveKeyboard } from './keyboardScroll';
import { Text } from './Text';

type ScreenProps = {
  title?: string;
  /** Hero moments (sign-in, welcome) get the display cut; page titles use title. */
  titleVariant?: 'title' | 'display';
  /** Content titles (an item's name) and screens with no navigator header show on the phone too. */
  showTitleOnPhone?: boolean;
  subtitle?: string;
  /** Catalog facts (format, platform, year) set in mono. */
  subtitleVariant?: 'body' | 'mono';
  children?: ReactNode;
  scroll?: boolean;
  /**
   * Form screens (#442): the scroll view sits in a KeyboardAvoidingView and
   * the focused field scrolls above the keyboard. Ignored when scroll is off.
   */
  keyboardAvoiding?: boolean;
};

export function Screen({
  title,
  titleVariant = 'title',
  showTitleOnPhone = false,
  subtitle,
  subtitleVariant = 'body',
  children,
  scroll = true,
  keyboardAvoiding = false,
}: ScreenProps) {
  // One title per screen: on the phone the navigator header names it, so the section title
  // only renders on the web. Pushed screens keep the nav-bar title on both platforms and pass
  // no title at all. The subtitle is content, not a title, and always shows.
  const showTitle = !!title && (showTitleOnPhone || Platform.OS === 'web');
  const content = (
    <>
      {showTitle || subtitle ? (
        <View style={styles.header}>
          {showTitle ? (
            <Text variant={titleVariant} accessibilityRole="header">
              {title}
            </Text>
          ) : null}
          {subtitle ? (
            <Text variant={subtitleVariant} tone="muted">
              {subtitle}
            </Text>
          ) : null}
        </View>
      ) : null}
      {children}
    </>
  );
  if (!scroll) {
    return <View style={[styles.fill, styles.padding]}>{content}</View>;
  }
  if (!keyboardAvoiding) {
    return (
      <ScrollView style={styles.fill} contentContainerStyle={styles.padding}>
        {content}
      </ScrollView>
    );
  }
  return <AvoidingScrollView>{content}</AvoidingScrollView>;
}

// The keyboard-aware-scroll-view pattern with built-ins only: the avoiding
// view keeps the scroll frame above the keyboard, and each focused field
// scrolls into view. Focus fires before the keyboard frame exists, so showing
// the keyboard re-scrolls to the last focused field.
function AvoidingScrollView({ children }: { children: ReactNode }) {
  const scrollRef = useRef<ScrollView>(null);
  const focusedRef = useRef<TextInput | null>(null);
  const reportFocus = useCallback((input: TextInput | null) => {
    focusedRef.current = input;
    scrollInputAboveKeyboard(scrollRef.current, input);
  }, []);
  useEffect(() => {
    if (Platform.OS === 'web') return;
    const show = Keyboard.addListener('keyboardDidShow', () => {
      scrollInputAboveKeyboard(scrollRef.current, focusedRef.current);
    });
    return () => show.remove();
  }, []);
  return (
    <InputFocusScope onFocus={reportFocus}>
      <KeyboardAvoidingView testID="keyboard-avoiding-view" style={styles.fill} behavior={keyboardAvoidingBehavior()}>
        <ScrollView
          ref={scrollRef}
          testID="keyboard-avoiding-scroll"
          style={styles.fill}
          contentContainerStyle={styles.padding}
          keyboardShouldPersistTaps="handled"
          contentInsetAdjustmentBehavior="automatic"
        >
          {children}
        </ScrollView>
      </KeyboardAvoidingView>
    </InputFocusScope>
  );
}

const styles = StyleSheet.create((theme) => ({
  header: { gap: theme.spacing.xs },
  fill: { flex: 1, backgroundColor: theme.colors.ground },
  padding: { padding: theme.spacing.lg, gap: theme.spacing.lg },
}));
