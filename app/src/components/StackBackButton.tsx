// The way back out of every pushed screen (#356): chevron + Back, a 44pt
// target, spoken as Back. Both stack layouts render it as their headerLeft, so
// a new pushed screen inherits it; the (pages) layout also renders it above
// the content on the web, where pages have no header. With nowhere to go back
// to (a fresh load), it lands on the parent hub instead of stranding the page.
// The scanner stays out: its own Close owns that flow.
import { type Href, usePathname, useRouter } from 'expo-router';
import { Pressable, StyleSheet } from 'react-native';
import Animated from 'react-native-reanimated';
import { Text, usePressFeedback } from '@/ui';

/** Where a fresh load of a pushed screen lands: the hub that links to it. */
function parentHub(pathname: string): Href {
  if (pathname === '/evals') return '/settings';
  if (pathname === '/settings' || pathname === '/members' || pathname === '/about' || pathname.startsWith('/me/')) return '/me';
  return '/library';
}

export function StackBackButton() {
  const router = useRouter();
  const pathname = usePathname();
  const press = usePressFeedback('none');
  if (pathname === '/scanner') return null;
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
        <Text variant="label" tone="accent">
          ‹ Back
        </Text>
      </Pressable>
    </Animated.View>
  );
}

const styles = StyleSheet.create({
  back: { minWidth: 44, minHeight: 44, justifyContent: 'center', alignSelf: 'flex-start' },
});
