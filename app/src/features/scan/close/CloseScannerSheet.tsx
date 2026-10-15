// The close-scanner sheet (docs/06 M3): shown over the scanner when items are waiting.
import { StyleSheet, View } from 'react-native';
import { spacing } from '@/theme/tokens';
import { Button, Sheet, Text } from '@/ui';
import type { CloseState } from './closeMachine';

type Props = {
  state: CloseState;
  unreviewed: number;
  onChoose(choice: 'review' | 'keep' | 'discard'): void;
  onDismiss(): void;
};

export const itemsCopy = (n: number) => `${n} ${n === 1 ? 'item' : 'items'}`;

export function CloseScannerSheet({ state, unreviewed, onChoose, onDismiss }: Props) {
  if (state.phase === 'idle') return null;
  const busy = state.phase === 'discarding';
  return (
    <View pointerEvents="box-none" style={[StyleSheet.absoluteFill, { justifyContent: 'flex-end', padding: spacing.lg }]}>
      <Sheet title="Close the scanner" onClose={onDismiss}>
        <Text>{`${itemsCopy(unreviewed)} scanned and not added yet. Review them now, keep them for later, or discard this scan. Discarding adds nothing to your library.`}</Text>
        {state.error ? (
          <Text tone="danger" accessibilityRole="alert">
            {state.error}
          </Text>
        ) : null}
        <Button label={`Review ${itemsCopy(unreviewed)}`} disabled={busy} onPress={() => onChoose('review')} />
        <Button label="Keep for later" variant="secondary" disabled={busy} onPress={() => onChoose('keep')} />
        <Button label="Discard" variant="danger" busy={busy} onPress={() => onChoose('discard')} />
      </Sheet>
    </View>
  );
}
