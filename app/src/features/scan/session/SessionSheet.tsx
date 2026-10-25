// The session sheet: where this scan's items go, their status, what is being scanned and where. Every capture
// inherits it; Review starts from it. Saving a home choice remembers it for the next session in that library.
// First: this module needs Unistyles configured before it runs (#305).
import '@/theme/unistyles';
import { useState } from 'react';
import { View } from 'react-native';
import { StyleSheet } from 'react-native-unistyles';
import { breadcrumb } from '@/features/item/breadcrumb';
import { flattenTree } from '@/features/locations/tree';
import { useLocationsList } from '@/features/locations/useLocations';
import { roleLabel } from '@/features/onboarding/JoinLibraryScreen';
import { useLibraries } from '@/features/onboarding/useLibraries';
import { useLibraryStore } from '@/state/library';
import { Button, Chip, ListRow, Sheet, Text } from '@/ui';
import {
  defaultSessionContext,
  rememberSessionContext,
  type SessionChannel,
  type SessionContext,
  type SessionItemStatus,
  type SessionMediaHint,
} from './sessionContext';

const STATUS_LABEL: Record<SessionItemStatus, string> = { owned: 'Owned', wishlist: 'Wishlist' };
const MEDIA_LABEL: Record<SessionMediaHint, string> = { books: 'Books', games: 'Games', movies: 'Movies', mixed: 'Mixed' };
const CHANNEL_LABEL: Record<SessionChannel, string> = { home: 'Home', in_store: 'In-store' };

type Props = {
  libraryId: string;
  context: SessionContext;
  onSave: (libraryId: string, context: SessionContext) => void;
  onClose: () => void;
};

export function SessionSheet({ libraryId, context, onSave, onClose }: Props) {
  const { libraries } = useLibraries();
  const setActive = useLibraryStore((s) => s.setActiveLibraryId);
  const [libId, setLibId] = useState(libraryId);
  const [draft, setDraft] = useState(context);
  const [picking, setPicking] = useState<'library' | 'location' | null>(null);
  const { locations } = useLocationsList(libId);
  const inStore = draft.channel === 'in_store';

  const libraryName = libraries.find((l) => l.id === libId)?.name ?? '';
  const place = inStore ? 'Decide later' : breadcrumb(locations, draft.locationId);

  const pickLibrary = (id: string) => {
    setLibId(id);
    setDraft(defaultSessionContext(id)); // the other library's remembered spot, or a blank home scan
    setPicking(null);
  };

  const save = () => {
    const next = inStore ? { ...draft, locationId: null } : draft; // in-store defers the location: Review starts at the last home spot
    setActive(libId);
    rememberSessionContext(libId, next);
    onSave(libId, next);
  };

  return (
    <Sheet title="Scan session" onClose={onClose}>
      <View style={styles.row}>
        <Text variant="label">Library</Text>
        <Button
          label={libraryName}
          accessibilityLabel={`Library: ${libraryName}. Change`}
          variant="secondary"
          onPress={() => setPicking(picking === 'library' ? null : 'library')}
        />
      </View>
      {picking === 'library' ? (
        <View style={styles.list} accessibilityRole="menu">
          {libraries.map((l) => (
            <ListRow key={l.id} title={l.name} subtitle={roleLabel[l.role]} onPress={() => pickLibrary(l.id)} />
          ))}
        </View>
      ) : null}

      <View style={styles.row}>
        <Text variant="label">Location</Text>
        {inStore ? (
          <Text accessibilityLabel="Location: decide later">Decide later</Text>
        ) : (
          <Button
            label={place}
            accessibilityLabel={`Location: ${place}. Change`}
            variant="secondary"
            onPress={() => setPicking(picking === 'location' ? null : 'location')}
          />
        )}
      </View>
      {picking === 'location' && !inStore ? (
        <View style={styles.list} accessibilityRole="menu">
          <ListRow title="Not placed" onPress={() => { setDraft((d) => ({ ...d, locationId: null })); setPicking(null); }} />
          {flattenTree(locations).map(({ location, depth }) => (
            <ListRow
              key={location.id}
              title={location.name}
              depth={depth}
              onPress={() => { setDraft((d) => ({ ...d, locationId: location.id })); setPicking(null); }}
            />
          ))}
        </View>
      ) : null}

      <View style={styles.row}>
        <Text variant="label">Status</Text>
        <View style={styles.chips}>
          {(['owned', 'wishlist'] as const).map((s) => (
            <Chip key={s} label={STATUS_LABEL[s]} selected={draft.status === s} onPress={() => setDraft((d) => ({ ...d, status: s }))} />
          ))}
        </View>
      </View>

      <View style={styles.row}>
        <Text variant="label">Media</Text>
        <View style={styles.chips}>
          {(['books', 'games', 'movies', 'mixed'] as const).map((m) => (
            <Chip key={m} label={MEDIA_LABEL[m]} selected={draft.mediaHint === m} onPress={() => setDraft((d) => ({ ...d, mediaHint: m }))} />
          ))}
        </View>
      </View>

      <View style={styles.row}>
        <Text variant="label">Channel</Text>
        <View style={styles.chips}>
          {(['home', 'in_store'] as const).map((c) => (
            <Chip key={c} label={CHANNEL_LABEL[c]} selected={draft.channel === c} onPress={() => setDraft((d) => ({ ...d, channel: c }))} />
          ))}
        </View>
      </View>

      <Button label="Save" onPress={save} />
    </Sheet>
  );
}

const styles = StyleSheet.create((theme) => ({
  row: { flexDirection: 'row', alignItems: 'center', gap: theme.spacing.md },
  chips: { flexDirection: 'row', flexWrap: 'wrap', gap: theme.spacing.sm },
  list: { gap: theme.spacing.xs },
}));
