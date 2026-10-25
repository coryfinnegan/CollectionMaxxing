// The session bar in the scanner chrome: what this scan inherits, in one line, with the sheet and the search a tap away.
// First: this module needs Unistyles configured before it runs (#305).
import '@/theme/unistyles';
import { Pressable, View } from 'react-native';
import { StyleSheet } from 'react-native-unistyles';
import { breadcrumb } from '@/features/item/breadcrumb';
import { useLocationsList } from '@/features/locations/useLocations';
import { Button, Text } from '@/ui';
import { SESSION_CHANNEL_LABEL, SESSION_MEDIA_LABEL, SESSION_STATUS_LABEL, type SessionContext } from '../session/sessionContext';

type Props = {
  libraryId: string;
  context: SessionContext;
  onOpenSession: () => void;
  onOpenSearch: () => void;
};

export function SessionBar({ libraryId, context, onOpenSession, onOpenSearch }: Props) {
  const { locations } = useLocationsList(libraryId);
  const place = context.channel === 'in_store' ? 'Decide later' : breadcrumb(locations, context.locationId);
  const summary = `${place} · ${SESSION_STATUS_LABEL[context.status]} · ${SESSION_MEDIA_LABEL[context.mediaHint]} · ${SESSION_CHANNEL_LABEL[context.channel]}`;
  return (
    <View style={styles.bar}>
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
      <Button label="Search" accessibilityLabel="Search the catalog" variant="secondary" onPress={onOpenSearch} style={styles.search} />
    </View>
  );
}

const styles = StyleSheet.create((theme) => ({
  bar: { flexDirection: 'row', alignItems: 'center', gap: theme.spacing.sm, paddingHorizontal: theme.spacing.lg },
  summary: {
    flex: 1,
    minHeight: 44,
    justifyContent: 'center',
    paddingHorizontal: theme.spacing.md,
    borderRadius: theme.radius.pill,
    backgroundColor: theme.fixed.scrim,
  },
  text: { color: theme.fixed.white },
  search: { backgroundColor: theme.fixed.scrim },
}));
