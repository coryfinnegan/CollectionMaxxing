// First: this module needs Unistyles configured before it runs (#305).
import '@/theme/unistyles';
import { View } from 'react-native';
import { StyleSheet } from 'react-native-unistyles';
import { Chip, Sheet } from '@/ui';
import { columnDefs, type ColumnId, toggleColumn } from './columns';

type Props = { columns: readonly ColumnId[]; onChange: (columns: ColumnId[]) => void; onClose: () => void };

/** Toggles which columns the table shows. Title stays on. */
export function ColumnChooser({ columns, onChange, onClose }: Props) {
  return (
    <Sheet title="Columns" onClose={onClose}>
      <View style={styles.wrap}>
        {columnDefs
          .filter((c) => c.id !== 'title')
          .map((column) => (
            <Chip
              key={column.id}
              label={column.label}
              accessibilityLabel={`${columns.includes(column.id) ? 'Hide' : 'Show'} ${column.label} column`}
              selected={columns.includes(column.id)}
              onPress={() => onChange(toggleColumn(columns, column.id))}
            />
          ))}
      </View>
    </Sheet>
  );
}

const styles = StyleSheet.create((theme) => ({
  wrap: { flexDirection: 'row', flexWrap: 'wrap', gap: theme.spacing.sm },
}));
