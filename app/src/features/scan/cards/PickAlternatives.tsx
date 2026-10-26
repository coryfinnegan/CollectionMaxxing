// A close call's alternatives on its card (design §8(b)): the same pick list Review
// shows, so a cover read is settled with one tap where it landed.
import { StyleSheet, View } from 'react-native';
import type { CandidateDto, ReleaseDto } from '@/api/generated/models';
import { formatLabels } from '@/features/library/labels';
import { alternativesOf } from '@/features/review/grouping';
import { spacing } from '@/theme/tokens';
import { Button } from '@/ui';

export function PickAlternatives({
  candidate,
  disabled,
  onPick,
}: {
  candidate: CandidateDto;
  disabled: boolean;
  onPick(candidate: CandidateDto, release: ReleaseDto): void;
}) {
  const alternatives = alternativesOf(candidate);
  if (alternatives.length === 0) return null;
  return (
    <View style={styles.list}>
      {alternatives.map((r) => (
        <Button
          key={r.id}
          label={`${r.title}${r.year ? ` (${r.year})` : ''} ${formatLabels[r.format] ?? ''}`.trim()}
          accessibilityLabel={`Pick ${r.title}`}
          variant="secondary"
          disabled={disabled}
          onPress={() => onPick(candidate, r)}
        />
      ))}
    </View>
  );
}

const styles = StyleSheet.create({
  list: { gap: spacing.sm },
});
