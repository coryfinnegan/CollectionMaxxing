// Web: the table by default, with a toggle to the grid (APP-1's grid, unchanged).
import { useState } from 'react';
import { View } from 'react-native';
import { StyleSheet } from 'react-native-unistyles';
import { LibraryTable } from '@/features/library-table/LibraryTable.web';
import { loadViewMode, saveViewMode, type ViewMode } from '@/features/library-table/viewMode';
import { Chip } from '@/ui';
import { LibraryGrid, type LibraryGridProps } from './LibraryGrid';

export type LibraryViewProps = LibraryGridProps;

export function LibraryView({ title, fixedStatus }: LibraryViewProps) {
  const [mode, setMode] = useState<ViewMode>(() => loadViewMode());
  const choose = (next: ViewMode) => {
    setMode(next);
    saveViewMode(next);
  };
  return (
    <View style={styles.root}>
      <View accessibilityRole="toolbar" accessibilityLabel="Library view" style={styles.toolbar}>
        <Chip label="Table" accessibilityLabel="Table view" selected={mode === 'table'} onPress={() => choose('table')} />
        <Chip label="Grid" accessibilityLabel="Grid view" selected={mode === 'grid'} onPress={() => choose('grid')} />
      </View>
      {mode === 'table' ? <LibraryTable title={title} fixedStatus={fixedStatus} /> : <LibraryGrid title={title} fixedStatus={fixedStatus} />}
    </View>
  );
}

const styles = StyleSheet.create((theme) => ({
  root: { flex: 1, backgroundColor: theme.colors.ground },
  toolbar: {
    flexDirection: 'row',
    gap: theme.spacing.sm,
    paddingHorizontal: theme.spacing.lg,
    paddingTop: theme.spacing.md,
    paddingBottom: theme.spacing.sm,
    backgroundColor: theme.colors.ground,
  },
}));
