// One place that speaks to the screen reader. iOS VoiceOver ignores accessibilityLiveRegion, so anything that
// appears without the person touching it (a scan, a card, a toast) is announced through here. On the web
// react-native-web writes the message into a polite live region.
import { AccessibilityInfo } from 'react-native';

export function announceForScreenReader(message: string): void {
  if (!message) return;
  try {
    AccessibilityInfo.announceForAccessibility(message);
  } catch {
    // Announcing is a courtesy; never let it break the screen.
  }
}
