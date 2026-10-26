// The session bar in the scanner chrome: what this scan inherits, in one line, with the sheet and the search a tap away.
// Adding by hand and the scans waiting from the last session sit beside it, so both stay one tap from the viewfinder.
// First: this module needs Unistyles configured before it runs (#305).
import '@/theme/unistyles';
import { Pressable, View } from 'react-native';
import { StyleSheet } from 'react-native-unistyles';
import { useStore } from 'zustand';
import { breadcrumb } from '@/features/item/breadcrumb';
import { useLocationsList } from '@/features/locations/useLocations';
import { Button, Text } from '@/ui';
import { summaryLine } from '../cards/copy';
import { SESSION_CHANNEL_LABEL, SESSION_MEDIA_LABEL, SESSION_STATUS_LABEL, type SessionContext } from '../session/sessionContext';
import { getTrayStore } from '../tray/storage';

type Props = {
  libraryId: string;
  context: SessionContext;
  onOpenSession: () => void;
  onOpenSearch: () => void;
  onAddManually: () => void;
  onOpenReview: () => void;
};

export function SessionBar({ libraryId, context, onOpenSession, onOpenSearch, onAddManually, onOpenReview }: Props) {
  const { locations } = useLocationsList(libraryId);
  // This library's tray only: another library's waiting scan is not yours to review here.
  const waiting = useStore(getTrayStore(libraryId), (s) => s.lines.reduce((sum, l) => sum + l.copies, 0));
  const place = context.channel === 'in_store' ? 'Decide later' : breadcrumb(locations, context.locationId);
  const summary = `${place} · ${SESSION_STATUS_LABEL[context.status]} · ${SESSION_MEDIA_LABEL[context.mediaHint]} · ${SESSION_CHANNEL_LABEL[context.channel]}`;
  return (
    <View style={styles.bar}>
      <View style={styles.row}>
        <Pressable
          accessibilityRole="button"
          accessibilityLabel={`Session: ${place}, ${SESSION_STATUS_LABEL[context.status]}, ${SESSION_MEDIA_LABEL[context.mediaHint]}, ${SESSION_CHANNEL_LABEL[context.channel]}. Change settings`}
          onPress={onOpenSession}
          style={styles.summary}
        >
          <Text style={styles.text} numberOfLines={1}>
            {summary}
          </Text>
        </Pressable>
        <Button label="Search" accessibilityLabel="Search the catalog" variant="secondary" onPress={onOpenSearch} style={styles.entry} />
      </View>
      <View style={styles.row}>
        <Button label="Add it by hand" variant="secondary" onPress={onAddManually} style={styles.entry} />
        {waiting > 0 ? (
          <Button
            label={`Waiting: ${summaryLine(waiting, 0)}`}
            accessibilityLabel={`Waiting from your last scan: ${summaryLine(waiting, 0)}. Open Review`}
            variant="secondary"
            onPress={onOpenReview}
            style={styles.entry}
          />
        ) : null}
      </View>
    </View>
  );
}

const styles = StyleSheet.create((theme) => ({
  bar: { gap: theme.spacing.sm },
  row: { flexDirection: 'row', alignItems: 'center', gap: theme.spacing.sm, paddingHorizontal: theme.spacing.lg },
  summary: {
    flex: 1,
    minHeight: 44,
    justifyContent: 'center',
    paddingHorizontal: theme.spacing.md,
    borderRadius: theme.radius.pill,
    backgroundColor: theme.fixed.scrim,
  },
  text: { color: theme.fixed.white },
  entry: { backgroundColor: theme.fixed.scrim },
}));
