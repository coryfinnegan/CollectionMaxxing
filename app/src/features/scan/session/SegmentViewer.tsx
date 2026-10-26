// The snap viewer: the strip's tap target. Pages across the scan's snaps in shoot order, each shown in the photo viewer
// with every item's state. The pictures are the phone's own stills; nothing here uploads anything.
// First: this module needs Unistyles configured before it runs (#305).
import '@/theme/unistyles';
import { useMemo, useState } from 'react';
import { View } from 'react-native';
import { StyleSheet } from 'react-native-unistyles';
import { useStore } from 'zustand';
import type { CandidateDto } from '@/api/generated/models';
import { Button } from '@/ui';
import { useCardsStore, withLocalCards } from '../cards/cardsStore';
import { PhotoViewer } from '../detail/PhotoViewer';
import { cardFor } from '../pipeline/lines';
import { getPipelineStore, type PicSnap } from '../pipeline/pipelineStore';
import { getSnapStore } from '../pipeline/snapStore';
import { getTrayStore, useTrayValue } from '../tray/storage';
import { cardStateLabel, inShootOrder, segmentLabel, segmentStateOf } from './segmentStates';

function contextOf(snap: PicSnap, cards: CandidateDto[]) {
  const stored = getSnapStore().get(snap.snapId);
  const peers = snap.items.flatMap((item) => {
    const card = cardFor(item, cards);
    return card ? [{ card, stored: stored?.items.find((i) => i.localId === item.localId) }] : [];
  });
  return { snapId: snap.snapId, snap: stored, peers };
}

export function SegmentViewer({ libraryId, initialSnapId, onClose }: { libraryId: string; initialSnapId: string; onClose(): void }) {
  const snaps = useStore(getPipelineStore(), (s) => s.snaps);
  const byId = useCardsStore((s) => s.byId);
  const local = useCardsStore((s) => s.local);
  const online = useTrayValue(getTrayStore(libraryId), (s) => s.online, true);
  const cards = useMemo(() => withLocalCards({ byId, local }), [byId, local]);
  const mine = useMemo(() => inShootOrder(snaps.filter((s) => s.libraryId === libraryId)), [snaps, libraryId]);
  const [index, setIndex] = useState(() => Math.max(0, mine.findIndex((s) => s.snapId === initialSnapId)));
  const [currentId, setCurrentId] = useState<string | null>(null);
  const snap = mine[Math.min(index, Math.max(0, mine.length - 1))];
  if (!snap) return null;
  const context = contextOf(snap, cards);
  const peerStates = Object.fromEntries(context.peers.map((p) => [p.card.id, cardStateLabel(p.card)]));
  const state = segmentLabel(segmentStateOf(snap, cards, online));
  const many = mine.length > 1;
  return (
    <View style={styles.sheet}>
      <PhotoViewer
        key={snap.snapId}
        context={context}
        currentId={currentId ?? context.peers[0]?.card.id ?? ''}
        onSelect={setCurrentId}
        peerStates={peerStates}
        heading={many ? `Snap ${index + 1} of ${mine.length} · ${state}` : `Snap · ${state}`}
      />
      {many ? (
        <View style={styles.pager}>
          <Button
            label="Previous snap"
            variant="secondary"
            onPress={() => {
              setIndex((index - 1 + mine.length) % mine.length);
              setCurrentId(null);
            }}
          />
          <Button
            label="Next snap"
            variant="secondary"
            onPress={() => {
              setIndex((index + 1) % mine.length);
              setCurrentId(null);
            }}
          />
        </View>
      ) : null}
      <Button label="Close" variant="secondary" onPress={onClose} />
    </View>
  );
}

const styles = StyleSheet.create((theme) => ({
  sheet: { gap: theme.spacing.md, padding: theme.spacing.lg, borderRadius: theme.radius.lg, backgroundColor: theme.colors.surface },
  pager: { flexDirection: 'row', justifyContent: 'space-between', gap: theme.spacing.sm },
}));
