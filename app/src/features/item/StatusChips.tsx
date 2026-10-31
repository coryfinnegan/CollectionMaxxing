// The edit form's status chips: the three that are in the library first, then a thin line, then the two that live beside it
// (Wishlist and Sold), so the form reads the way the rest of the app does. Renders into the form group's chip row.
// First: this module needs Unistyles configured before it runs (#305).
import '@/theme/unistyles';
import { View } from 'react-native';
import { StyleSheet } from 'react-native-unistyles';
import type { ItemStatus } from '@/api/generated/models';
import { statusLabels } from '@/features/library/labels';
import { BESIDE_COLLECTION, IN_COLLECTION } from '@/features/library/statuses';
import { Chip } from '@/ui';

export function StatusChips({ value, onChange }: { value: ItemStatus; onChange: (status: ItemStatus) => void }) {
  const chip = (status: ItemStatus) => (
    <Chip
      key={status}
      label={statusLabels[status]}
      accessibilityLabel={`Status ${statusLabels[status]}`}
      selected={value === status}
      onPress={() => onChange(status)}
    />
  );
  return (
    <>
      {IN_COLLECTION.map(chip)}
      <View testID="status-separator" accessibilityElementsHidden importantForAccessibility="no-hide-descendants" style={styles.separator} />
      {BESIDE_COLLECTION.map(chip)}
    </>
  );
}

const styles = StyleSheet.create((theme) => ({
  separator: { width: 1, alignSelf: 'stretch', marginHorizontal: theme.spacing.xs, backgroundColor: theme.colors.line },
}));
