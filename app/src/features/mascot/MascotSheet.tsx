// The mascot contact sheet (#365): all ten poses side by side. A dev fixture for
// the Playwright spec and eyeballing; linked nowhere, so no screen changes for
// anyone else.
// First: this module needs Unistyles configured before it runs (#305).
import '@/theme/unistyles';
import type { ReactNode } from 'react';
import { View } from 'react-native';
import { StyleSheet } from 'react-native-unistyles';
import { Screen, Text } from '@/ui';
import { Mascot, poseIds, type MascotPose } from '@/ui/mascot';

const captions: Record<MascotPose, string> = {
  idle: 'Idle perch',
  reading: 'Reading',
  sleeping: 'Sleeping',
  celebrating: 'Celebrating',
  searching: 'Searching',
  emptyHoard: 'Empty hoard',
  oops: 'Nose puff',
  milestone: 'Milestone',
  fastReading: 'Fast reading',
  refreshPeek: 'Pull to refresh peek',
};

function Cell({ id, caption, children }: { id: string; caption: string; children: ReactNode }) {
  return (
    <View style={styles.cell} testID={id}>
      <Text variant="caption" tone="muted">
        {caption}
      </Text>
      {children}
    </View>
  );
}

export function MascotSheet() {
  return (
    <Screen title="Mascot contact sheet" subtitle="Every dragon pose, sharing one set of layers." scroll={false}>
      <View style={styles.grid}>
        {poseIds.map((pose) => (
          <Cell key={pose} id={`mascot-cell-${pose}`} caption={captions[pose]}>
            <Mascot pose={pose} size={200} />
          </Cell>
        ))}
      </View>
    </Screen>
  );
}

const styles = StyleSheet.create((theme) => ({
  grid: { flexDirection: 'row', flexWrap: 'wrap', gap: theme.spacing.xl },
  cell: { gap: theme.spacing.sm },
}));
