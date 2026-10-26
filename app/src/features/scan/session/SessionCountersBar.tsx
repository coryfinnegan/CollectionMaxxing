// The live counters above the receipt (docs/16 §5): captured, added, to review, already owned. Offline, the bar shows
// the local queue against the last snapshot it saw, badged "offline".
// First: this module needs Unistyles configured before it runs (#305).
import '@/theme/unistyles';
import { View } from 'react-native';
import { StyleSheet } from 'react-native-unistyles';
import { fixed } from '@/theme/tokens';
import { Text } from '@/ui';
import { useSessionCounters } from './useSessionCounters';

export function SessionCountersBar({ libraryId, sessionId }: { libraryId: string; sessionId: string | null }) {
  const { counters, offline } = useSessionCounters(libraryId, sessionId);
  const line = `${counters.captured} captured · ${counters.added} added · ${counters.toReview} to review · ${counters.owned} owned`;
  return (
    <View style={styles.bar}>
      <Text
        accessibilityRole="text"
        accessibilityLabel={`${line}${offline ? '. Offline: counts include scans still on this phone' : ''}`}
        accessibilityLiveRegion="polite"
        style={styles.text}
        numberOfLines={1}
      >
        {line}
      </Text>
      {offline ? (
        <View style={styles.badge} accessibilityLabel="Offline">
          <Text style={styles.badgeText}>Offline</Text>
        </View>
      ) : null}
    </View>
  );
}

const styles = StyleSheet.create((theme) => ({
  bar: {
    flexDirection: 'row',
    alignItems: 'center',
    gap: theme.spacing.sm,
    marginHorizontal: theme.spacing.lg,
    paddingHorizontal: theme.spacing.md,
    minHeight: 36,
    borderRadius: theme.radius.pill,
    backgroundColor: theme.fixed.scrim,
  },
  text: { flex: 1, color: fixed.white },
  badge: { borderRadius: theme.radius.pill, backgroundColor: fixed.white, paddingHorizontal: theme.spacing.sm, paddingVertical: 2 },
  badgeText: { color: fixed.black },
}));
