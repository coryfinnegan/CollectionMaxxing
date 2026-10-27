// The scanner's bottom bar (#451): session counts plus the Review button. Review lists every pending item;
// nothing expands here and there is no undo — the bar is counts plus one obvious action.
import { useEffect, useRef } from 'react';
import { StyleSheet, View } from 'react-native';
import { announceForScreenReader } from '@/lib/a11y/announce';
import { fixed, radius, spacing, useColors } from '@/theme/tokens';
import { Button, GlassSurface, Text } from '@/ui';
import { reviewButtonLabel, reviewStatusLine, summaryLine } from '../cards/copy';
import type { SessionCounters } from '../session/sessionCounters';

export function ReceiptBar({
  online,
  items,
  processing,
  problem,
  notice,
  counters,
  queued = 0,
  onReview,
}: {
  online: boolean;
  items: number;
  processing: number;
  problem: string | null;
  notice: string | null;
  /** The session counters: when present, the bar shows the merged one-line status (#408). */
  counters?: SessionCounters;
  /** Scans waiting on this phone for a connection; VoiceOver announces the count as it grows. */
  queued?: number;
  onReview(): void;
}) {
  const colors = useColors();
  const status = counters ? reviewStatusLine(items, processing, counters.added, counters.toReview, counters.owned) : summaryLine(items, processing);
  // VoiceOver ignores live regions: say "Queued" when a scan lands with no connection to send it.
  const spokenQueued = useRef(0);
  useEffect(() => {
    if (queued > spokenQueued.current) announceForScreenReader(`Queued. ${queued} ${queued === 1 ? 'item' : 'items'} waiting for your server.`);
    spokenQueued.current = queued;
  }, [queued]);
  return (
    // A frosted dock over the camera on iOS 26+, the surface bar elsewhere (#306).
    <GlassSurface style={[styles.dock, { borderTopColor: colors.line }]} fallbackStyle={{ backgroundColor: colors.surface }} tint={colors.surface}>
      {notice ? (
        <Text variant="caption" tone="muted" style={styles.message} accessibilityLiveRegion="polite">
          {notice}
        </Text>
      ) : null}
      {problem ? (
        <Text variant="caption" tone="danger" style={styles.message} accessibilityLiveRegion="polite">
          {problem}
        </Text>
      ) : null}
      {!online ? (
        <Text variant="caption" tone="muted" style={styles.message} accessibilityLiveRegion="polite">
          Couldn&apos;t reach your server. Your scans are saved and will finish when you&apos;re back online.
        </Text>
      ) : null}
      <View style={styles.row}>
        <Text variant="label" accessibilityLiveRegion="polite" numberOfLines={1} style={styles.status}>
          {status}
        </Text>
        {!online ? (
          <View style={styles.offlineBadge} accessibilityLabel="Offline">
            <Text variant="label" style={styles.offlineText}>
              Offline
            </Text>
          </View>
        ) : null}
        <Button label={reviewButtonLabel(items)} onPress={onReview} disabled={items === 0} />
      </View>
    </GlassSurface>
  );
}

const styles = StyleSheet.create({
  dock: { borderTopWidth: 1 },
  message: { paddingHorizontal: spacing.lg, paddingTop: spacing.sm },
  row: { flexDirection: 'row', alignItems: 'center', justifyContent: 'space-between', padding: spacing.lg, gap: spacing.md },
  status: { flex: 1 },
  offlineBadge: { borderRadius: radius.pill, backgroundColor: fixed.white, paddingHorizontal: spacing.sm, paddingVertical: 2 },
  offlineText: { color: fixed.black },
});
