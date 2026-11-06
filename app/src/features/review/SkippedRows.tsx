// The scan's skipped rows (#709): collapsed at the end of Review. Each says what was read and why it was skipped, and can be put back.
// First: this module needs Unistyles configured before it runs (#305).
import '@/theme/unistyles';
import { useState } from 'react';
import { View } from 'react-native';
import { StyleSheet } from 'react-native-unistyles';
import type { CandidateDto } from '@/api/generated/models';
import { Button, Card, Text } from '@/ui';
import { skipReasonLabel, skippedTitleOf, type SkippedRow } from './skipped';

/** One line for the whole section: "3 items skipped. Show them", "1 item skipped. Show it". */
export const skippedLabel = (count: number): string =>
  `${count} ${count === 1 ? 'item' : 'items'} skipped. ${count === 1 ? 'Show it' : 'Show them'}`;

export function SkippedRows({
  rows,
  canPutBack,
  busy,
  errors,
  onPutBack,
}: {
  rows: readonly SkippedRow[];
  canPutBack: boolean;
  busy: Record<string, boolean>;
  errors: Record<string, string>;
  onPutBack(c: CandidateDto): void;
}) {
  const [open, setOpen] = useState(false);
  if (rows.length === 0) return null;
  return (
    <View style={styles.section}>
      <Text variant="heading" accessibilityRole="header">{`Skipped · ${rows.length}`}</Text>
      {open ? (
        <>
          {rows.map(({ candidate, reason }) => {
            const title = skippedTitleOf(candidate);
            return (
              <Card key={candidate.id}>
                <View style={styles.row}>
                  <View style={styles.what}>
                    <Text>{title}</Text>
                    <Text variant="caption" tone="muted">
                      {skipReasonLabel(reason)}
                    </Text>
                  </View>
                  {canPutBack ? (
                    <Button
                      label="Put back"
                      accessibilityLabel={`Put back ${title}`}
                      variant="secondary"
                      busy={!!busy[candidate.id]}
                      onPress={() => onPutBack(candidate)}
                    />
                  ) : null}
                </View>
                {errors[candidate.id] ? (
                  <Text tone="danger" accessibilityRole="alert">
                    {errors[candidate.id]}
                  </Text>
                ) : null}
              </Card>
            );
          })}
          <Button label="Hide skipped items" variant="secondary" onPress={() => setOpen(false)} />
        </>
      ) : (
        <Button label={skippedLabel(rows.length)} variant="secondary" onPress={() => setOpen(true)} />
      )}
    </View>
  );
}

const styles = StyleSheet.create((theme) => ({
  section: { gap: theme.spacing.sm },
  row: { flexDirection: 'row', alignItems: 'center', gap: theme.spacing.md },
  what: { flex: 1 },
}));
