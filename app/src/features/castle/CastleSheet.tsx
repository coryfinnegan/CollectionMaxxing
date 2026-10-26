// The castle contact sheet (#361): every primitive in Full, Flat and Accessible,
// side by side. A dev fixture for the Playwright spec and Phase 1 eyeballing;
// linked nowhere, so no screen changes for anyone else.
// First: this module needs Unistyles configured before it runs (#305).
import '@/theme/unistyles';
import type { ReactNode } from 'react';
import { View } from 'react-native';
import { StyleSheet } from 'react-native-unistyles';
import { Screen, Text } from '@/ui';
import { BannerMarker, CastleHeader, LedgeRow, ParchmentCard, TabletHeader, type CastleMode } from '@/ui/castle';

const cellModes: { mode: CastleMode; caption: string }[] = [
  { mode: 'full', caption: 'Full' },
  { mode: 'flat', caption: 'Flat' },
  { mode: 'accessible', caption: 'Accessible' },
];

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

export function CastleSheet() {
  return (
    <Screen title="Castle contact sheet" subtitle="Every primitive in every mode." scroll={false}>
      <Text variant="heading">Castle header</Text>
      {cellModes.map(({ mode, caption }) => (
        <Cell key={mode} id={`castle-cell-header-${mode}`} caption={caption}>
          <CastleHeader title="Gatehouse" subtitle="Who goes there" mode={mode} />
        </Cell>
      ))}
      <Text variant="heading">Tablet header</Text>
      {cellModes.map(({ mode, caption }) => (
        <Cell key={mode} id={`castle-cell-tablet-${mode}`} caption={caption}>
          <TabletHeader title="Armory" subtitle="Swords and shields" mode={mode} />
        </Cell>
      ))}
      <Text variant="heading">Banner marker</Text>
      {cellModes.map(({ mode, caption }) => (
        <Cell key={mode} id={`castle-cell-banner-${mode}`} caption={caption}>
          <BannerMarker label="New" mode={mode} />
        </Cell>
      ))}
      <Text variant="heading">Ledge row</Text>
      {cellModes.map(({ mode, caption }) => (
        <Cell key={mode} id={`castle-cell-ledge-${mode}`} caption={caption}>
          <LedgeRow title="Great hall" subtitle="Dinner at six" mode={mode} />
        </Cell>
      ))}
      <Text variant="heading">Parchment card</Text>
      {cellModes.map(({ mode, caption }) => (
        <Cell key={mode} id={`castle-cell-parchment-${mode}`} caption={caption}>
          <ParchmentCard title="Supplies" body="Rope, torches, chalk." mode={mode} />
        </Cell>
      ))}
    </Screen>
  );
}

const styles = StyleSheet.create((theme) => ({
  cell: { gap: theme.spacing.sm },
}));
