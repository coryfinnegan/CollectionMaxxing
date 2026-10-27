// The way back out of every pushed screen (#356, #445): a plain chevron glyph,
// a 44pt target, spoken as Back. Both stack layouts render it as their header
// back — inside the shared custom header on the phone, as the headerLeft and
// the in-flow row on the web — so a new pushed screen inherits it. With nowhere
// to go back to (a fresh load), it lands on the parent hub instead of stranding
// the page. The scanner stays out: its own Close owns that flow (an empty slot
// keeps the header title centered there).
import { type Href, usePathname, useRouter } from 'expo-router';
import { Pressable, StyleSheet, View } from 'react-native';
import Animated from 'react-native-reanimated';
import Svg, { Path } from 'react-native-svg';
import { useColors } from '@/theme/tokens';
import { usePressFeedback } from '@/ui';

/** Where a fresh load of a pushed screen lands: the hub that links to it. */
function parentHub(pathname: string): Href {
  if (pathname === '/evals') return '/settings';
  if (pathname === '/settings' || pathname === '/members' || pathname === '/about' || pathname.startsWith('/me/')) return '/me';
  return '/library';
}

/** A plain left chevron, hand-drawn SVG in the repo's glyph idiom (22px on a 24 grid). */
function ChevronGlyph({ color }: { color: string }) {
  return (
    <Svg width={22} height={22} viewBox="0 0 24 24">
      <Path d="M14.5 5.5 8 12l6.5 6.5" fill="none" stroke={color} strokeWidth={2.5} strokeLinecap="round" strokeLinejoin="round" />
    </Svg>
  );
}

export function StackBackButton() {
  const router = useRouter();
  const pathname = usePathname();
  const colors = useColors();
  const press = usePressFeedback('none');
  if (pathname === '/scanner') return <View style={styles.slot} />;
  const goBack = () => {
    if (router.canGoBack()) router.back();
    else router.replace(parentHub(pathname));
  };
  return (
    <Animated.View style={press.style}>
      <Pressable
        accessibilityRole="button"
        accessibilityLabel="Back"
        onPress={goBack}
        onPressIn={press.onPressIn}
        onPressOut={press.onPressOut}
        style={styles.back}
      >
        <ChevronGlyph color={colors.accent} />
      </Pressable>
    </Animated.View>
  );
}

const styles = StyleSheet.create({
  back: { minWidth: 44, minHeight: 44, justifyContent: 'center', alignItems: 'center', alignSelf: 'flex-start' },
  slot: { width: 44, height: 44 },
});
